namespace ClusterMapDownloader;

/// <summary>Metadata about a downloaded file.</summary>
public sealed record DownloadedFile
{
    public required string RelativeFileName { get; init; }
    public required string AbsolutePath { get; init; }
    public required long Bytes { get; init; }
    public required string Url { get; init; }
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
    /// expected size.
    /// </summary>
    public static async Task<DownloadedFile> DownloadToAsync(
        HttpClient client,
        AssetDownload asset,
        string outputDirectory,
        IProgressNotifier notifier,
        CancellationToken cancellationToken = default)
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
                return new DownloadedFile
                {
                    RelativeFileName = fileName,
                    AbsolutePath = targetPath,
                    Bytes = expected,
                    Url = asset.Url.ToString(),
                };
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

                await using (var targetStream = File.Create(partPath))
                {
                    await response.Content.CopyToAsync(targetStream, cancellationToken).ConfigureAwait(false);
                }

                File.Move(partPath, targetPath, overwrite: true);
                var bytes = new FileInfo(targetPath).Length;
                notifier.Success($"Downloaded {fileName} ({FormatBytes(bytes)} bytes) -> {targetPath}");
                return new DownloadedFile
                {
                    RelativeFileName = fileName,
                    AbsolutePath = targetPath,
                    Bytes = bytes,
                    Url = asset.Url.ToString(),
                };
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

    /// <summary>Downloads a batch of assets sequentially, reporting progress.</summary>
    public static async Task<IReadOnlyList<DownloadedFile>> DownloadAllAsync(
        HttpMessageHandler handler,
        IReadOnlyList<AssetDownload> assets,
        string outputDirectory,
        IProgressNotifier notifier,
        CancellationToken cancellationToken = default)
    {
        var results = new List<DownloadedFile>();
        using var client = new HttpClient(handler);
        client.Timeout = Timeout.InfiniteTimeSpan; // per-request concern; long streams must not be cut short
        foreach (var asset in assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await DownloadToAsync(client, asset, outputDirectory, notifier, cancellationToken)
                .ConfigureAwait(false));
        }
        return results;
    }

    internal static string FormatBytes(long bytes)
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
