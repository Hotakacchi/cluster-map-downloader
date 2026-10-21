namespace ClusterMapDownloader;

/// <summary>Metadata about a downloaded file.</summary>
public sealed record DownloadedFile
{
    public required string RelativeFileName { get; init; }
    public required string AbsolutePath { get; init; }
    public required long Bytes { get; init; }
    public required string Url { get; init; }

    /// <summary>Leading-byte classification (unityfs-plain / unknown-probably-encrypted);
    /// honest labeling only — non-plain files are saved as-is, never decrypted.</summary>
    public required string FileFormat { get; init; }

    /// <summary>Lowercase hex SHA-256 of the saved file (manifest integrity column).</summary>
    public required string Sha256 { get; init; }
}

/// <summary>
/// Generic, resumable-ish file download plumbing. Used today by tests and
/// ready for the future delivery resolver; it never decides *what* to
/// download — only *how* to download it safely.
/// </summary>
public static class FileDownloader
{
    public const int MaxAttempts = 3;

    /// <summary>
    /// Validates and sanitizes a relative file name that came from external data.
    /// Rejects path traversal, drive/root references, and absolute paths.
    /// </summary>
    public static string SanitizeRelativeFileName(string relativeFileName)
    {
        if (string.IsNullOrWhiteSpace(relativeFileName))
        {
            throw new ClusterApiException("Asset file name is empty.");
        }

        var name = relativeFileName.Replace('\\', '/');
        if (name.StartsWith('/'))
        {
            throw new ClusterApiException($"Unsafe asset file name (absolute path): '{relativeFileName}'.");
        }

        var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p == "." || p == ".."))
        {
            throw new ClusterApiException($"Unsafe asset file name: '{relativeFileName}'.");
        }

        foreach (var part in parts)
        {
            var invalid = part.IndexOfAny(Path.GetInvalidFileNameChars());
            if (invalid >= 0)
            {
                throw new ClusterApiException($"Unsafe asset file name: '{relativeFileName}'.");
            }
        }

        return string.Join('/', parts);
    }

    /// <summary>
    /// Downloads one asset into <paramref name="outputDirectory"/>, retrying
    /// transient failures. Writes to a <c>.part</c> temp file first and moves
    /// it into place on success. Skips files already present with the
    /// expected size. Byte deltas flow into <paramref name="byteProgress"/>
    /// when the caller passes one; the saved file is classified and hashed.
    /// </summary>
    public static async Task<DownloadedFile> DownloadToAsync(
        HttpClient client,
        AssetDownload asset,
        string outputDirectory,
        IProgressNotifier notifier,
        CancellationToken cancellationToken = default,
        IProgress<DownloadByteProgress>? byteProgress = null)
    {
        var fileName = SanitizeRelativeFileName(asset.RelativeFileName);
        var targetPath = Path.Combine(outputDirectory, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        if (asset.ExpectedSizeBytes is long expected && File.Exists(targetPath))
        {
            var existing = new FileInfo(targetPath);
            if (existing.Length == expected)
            {
                notifier.Info($"Already present, skipping (size matches): {fileName} ({expected} bytes).");
                return DescribeFile(fileName, targetPath, expected, asset.Url.ToString(), notifier);
            }
        }

        var partPath = targetPath + ".part";
        Exception? lastError = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var response = await client
                    .GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength;
                await using (var targetStream = File.Create(partPath))
                using (var sourceStream = await response.Content
                           .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                {
                    var buffer = new byte[1024 * 512];
                    long written = 0;
                    int read;
                    while ((read = await sourceStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await targetStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                            .ConfigureAwait(false);
                        written += read;
                        byteProgress?.Report(new DownloadByteProgress(written, totalBytes));
                    }
                }

                File.Move(partPath, targetPath, overwrite: true);
                var bytes = new FileInfo(targetPath).Length;
                notifier.Success($"Downloaded {fileName} ({FormatBytes(bytes)}) -> {targetPath}");
                return DescribeFile(fileName, targetPath, bytes, asset.Url.ToString(), notifier);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                lastError = ex;
                notifier.Warn($"Download of {fileName} failed (attempt {attempt}/{MaxAttempts}): {ex.Message}");
                if (attempt < MaxAttempts)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1 + attempt), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        throw new ClusterApiException(
            $"Failed to download {asset.Url} after {MaxAttempts} attempts: {lastError?.Message}", lastError);
    }

    private static DownloadedFile DescribeFile(string fileName, string absolutePath, long bytes, string url,
        IProgressNotifier notifier)
    {
        var format = AssetFileFormatInspector.Inspect(absolutePath);
        var sha256 = AssetFileFormatInspector.ComputeSha256(absolutePath);
        notifier.Info($"   format: {AssetFileFormatInspector.Describe(format)}  sha256: {sha256[..12]}…");
        return new DownloadedFile
        {
            RelativeFileName = fileName,
            AbsolutePath = absolutePath,
            Bytes = bytes,
            Url = url,
            FileFormat = AssetFileFormatInspector.ToManifestValue(format),
            Sha256 = sha256,
        };
    }

    /// <summary>Downloads a batch of assets sequentially, reporting progress.</summary>
    public static async Task<IReadOnlyList<DownloadedFile>> DownloadAllAsync(
        HttpMessageHandler handler,
        IReadOnlyList<AssetDownload> assets,
        string outputDirectory,
        IProgressNotifier notifier,
        CancellationToken cancellationToken = default,
        IProgress<DownloadedFileProgress>? fileProgress = null)
    {
        var results = new List<DownloadedFile>();
        using var client = new HttpClient(handler);
        client.Timeout = Timeout.InfiniteTimeSpan; // per-request concern; long streams must not be cut short
        for (var index = 0; index < assets.Count; index++)
        {
            var asset = assets[index];
            cancellationToken.ThrowIfCancellationRequested();
            fileProgress?.Report(new DownloadedFileProgress(asset.RelativeFileName, index + 1, assets.Count, null));
            var byteProgress = new Progress<DownloadByteProgress>(p =>
                fileProgress?.Report(new DownloadedFileProgress(asset.RelativeFileName, index + 1, assets.Count, p)));
            results.Add(await DownloadToAsync(client, asset, outputDirectory, notifier, cancellationToken,
                    byteProgress)
                .ConfigureAwait(false));
            fileProgress?.Report(new DownloadedFileProgress(asset.RelativeFileName, index + 1, assets.Count,
                new DownloadByteProgress(1, 1)));
        }

        return results;
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return FormattableString.Invariant($"{value:0.##} {units[unit]}");
    }
}

/// <summary>Byte-level download progress: the bytes written so far plus the
/// response's total size when the server advertised one (null otherwise).</summary>
public readonly record struct DownloadByteProgress(long DownloadedBytes, long? TotalBytes);

/// <summary>Coarse (per-file) + fine (byte-level) progress snapshot across a batch.</summary>
public readonly record struct DownloadedFileProgress(
    string RelativeFileName,
    int CurrentFile,
    int TotalFiles,
    DownloadByteProgress? Bytes);
