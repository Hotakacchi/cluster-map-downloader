using ClusterMapDownloader;
using Xunit;

namespace ClusterMapDownloader.Tests;

public class FileDownloaderTests
{
    [Fact]
    public void Sanitize_rejects_path_traversal()
    {
        foreach (var name in new[]
                 {
                     "", " ", ".", "..", "../x.bin", "a/../b.bin", "c:\\absolute.bin", "/root.bin",
                     "bad|name.bin", "bad?name.bin",
                 })
        {
            Assert.Throws<ClusterApiException>(() => FileDownloader.SanitizeRelativeFileName(name));
        }
    }

    [Fact]
    public void Sanitize_allows_normal_relative_names()
    {
        Assert.Equal("a/b/c.bin", FileDownloader.SanitizeRelativeFileName("a\\b\\c.bin"));
        Assert.Equal("file.bin", FileDownloader.SanitizeRelativeFileName("file.bin"));
        Assert.Equal("a/b.bin", FileDownloader.SanitizeRelativeFileName("a//b.bin/"));
        Assert.Throws<ClusterApiException>(
            () => FileDownloader.SanitizeRelativeFileName("/absolute/at/repo/root.bin"));
    }

    [Fact]
    public async Task Downloads_and_writes_file()
    {
        var bytes = "hello-asset-bytes"u8.ToArray();
        var handler = HttpResponseFactory.BufferHandler(bytes);
        var outDir = NewTempDir();
        try
        {
            var notifier = new RecordingNotifier();
            var files = await FileDownloader.DownloadAllAsync(
                handler,
                new[] { new AssetDownload(new Uri("https://assets.example.invalid/bin/thing.bin"), "assets/thing.bin") },
                outDir,
                notifier);

            var file = Assert.Single(files);
            Assert.Equal("assets/thing.bin", file.RelativeFileName);
            Assert.Equal(bytes.Length, file.Bytes);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(file.AbsolutePath));
        }
        finally
        {
            TryDeleteDir(outDir);
        }
    }

    [Fact]
    public async Task Skips_existing_file_when_size_matches()
    {
        var bytes = "existing-content"u8.ToArray();
        var outDir = NewTempDir();
        try
        {
            var existing = Path.Combine(outDir, "assets", "thing.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
            await File.WriteAllBytesAsync(existing, bytes);

            var handler = HttpResponseFactory.BufferHandler("SHOULD NOT BE WRITTEN"u8.ToArray());
            var notifier = new RecordingNotifier();
            var files = await FileDownloader.DownloadAllAsync(
                handler,
                new[]
                {
                    new AssetDownload(new Uri("https://assets.example.invalid/bin/thing.bin"),
                        "assets/thing.bin", ExpectedSizeBytes: bytes.Length),
                },
                outDir,
                notifier);

            var file = Assert.Single(files);
            Assert.Equal(bytes.Length, file.Bytes);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(file.AbsolutePath));
            Assert.Contains(notifier.Messages, m => m.Contains("Already present, skipping"));
        }
        finally
        {
            TryDeleteDir(outDir);
        }
    }

    [Fact]
    public async Task Fails_after_retries_on_http_error()
    {
        var handler = HttpResponseFactory.StatusHandler(500);
        var outDir = NewTempDir();
        try
        {
            var notifier = new RecordingNotifier();
            await Assert.ThrowsAsync<ClusterApiException>(() =>
                FileDownloader.DownloadAllAsync(
                    handler,
                    new[]
                    {
                        new AssetDownload(new Uri("https://assets.example.invalid/bin/no.bin"), "assets/no.bin"),
                    },
                    outDir,
                    notifier));
            // Retry warnings were logged for attempts < MaxAttempts.
            Assert.InRange(notifier.Entries.Count(e => e.Message.Contains("failed (attempt")),
                FileDownloader.MaxAttempts - 1, FileDownloader.MaxAttempts);
        }
        finally
        {
            TryDeleteDir(outDir);
        }
    }

    internal static string NewTempDir()
        => Path.Combine(Path.GetTempPath(), "cmd-tests", Guid.NewGuid().ToString("N"));

    internal static void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Best effort cleanup; ignore failures on locked temp files.
        }
    }
}
