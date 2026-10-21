using System.Security.Cryptography;
using ClusterMapDownloader;
using Xunit;

namespace ClusterMapDownloader.Tests;

/// <summary>
/// Honest-labeling tests for the leading-byte file format inspector plus the
/// download pipeline's manifest columns (fileFormat/sha256). Synthetic bytes
/// only; the encrypted case is always saved as-is, never decrypted.
/// </summary>
public class AssetFileFormatInspectorTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cmd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    private static string WriteFile(string dir, string name, byte[] bytes)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void Inspect_detects_unity_fs_magic()
    {
        var dir = TempDir();
        try
        {
            // 0x "UnityFS\0" + plausible header tail, exactly like a real bundle's lead.
            var bytes = new byte[]
            {
                0x55, 0x6E, 0x69, 0x74, 0x79, 0x46, 0x53, 0x00, 0x00, 0x00, 0x00, 0x08, 0x35, 0x2E, 0x78, 0x2E,
            };
            Assert.Equal(AssetFileFormat.UnityFsPlain,
                AssetFileFormatInspector.Inspect(WriteFile(dir, "a.bundle", bytes)));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Inspect_flags_non_unity_files_as_encrypted_or_unknown()
    {
        var dir = TempDir();
        try
        {
            Assert.Equal(AssetFileFormat.EncryptedOrUnknown,
                AssetFileFormatInspector.Inspect(WriteFile(dir, "plain.txt", "hello"u8.ToArray())));

            // AES-AVATAR-style layout (IV-prefixed ciphertext, no magic): high-entropy bytes.
            var encrypted = new byte[64];
            Random.Shared.NextBytes(encrypted);
            Assert.Equal(AssetFileFormat.EncryptedOrUnknown,
                AssetFileFormatInspector.Inspect(WriteFile(dir, "enc.bin", encrypted)));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Inspect_empty_and_missing_files_do_not_crash()
    {
        var dir = TempDir();
        try
        {
            Assert.Equal(AssetFileFormat.EncryptedOrUnknown,
                AssetFileFormatInspector.Inspect(WriteFile(dir, "empty.bin", Array.Empty<byte>())));
            Assert.Equal(AssetFileFormat.EncryptedOrUnknown,
                AssetFileFormatInspector.Inspect(Path.Combine(dir, "does-not-exist.bin")));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Manifest_values_are_stable_machine_names()
    {
        Assert.Equal("unityfs-plain",
            AssetFileFormatInspector.ToManifestValue(AssetFileFormat.UnityFsPlain));
        Assert.Equal("unknown-probably-encrypted",
            AssetFileFormatInspector.ToManifestValue(AssetFileFormat.EncryptedOrUnknown));
    }

    [Fact]
    public async Task DownloadAddsFormatAndSha256Columns()
    {
        var bytes = "UnityFS\0-fake-lead-only"u8.ToArray();
        var outDir = TempDir();
        try
        {
            var files = await FileDownloader.DownloadAllAsync(
                HttpResponseFactory.BufferHandler(bytes),
                [new AssetDownload(new Uri("https://assets.example.invalid/bin/scene.bundle"), "bundles/scene.bundle")],
                outDir,
                NullProgressNotifier.Instance);

            var single = Assert.Single(files);
            Assert.Equal("unityfs-plain", single.FileFormat);
            Assert.Equal(64, single.Sha256.Length);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), single.Sha256);
        }
        finally
        {
            TryDelete(outDir);
        }
    }

    [Fact]
    public async Task NonUnityDownloadIsFlaggedNotDecrypted()
    {
        var outDir = TempDir();
        try
        {
            var files = await FileDownloader.DownloadAllAsync(
                HttpResponseFactory.BufferHandler([0x12, 0x34, 0xAB, 0xCD]),
                [new AssetDownload(new Uri("https://assets.example.invalid/bin/asset.bin"), "bundles/asset.bin")],
                outDir,
                NullProgressNotifier.Instance);

            Assert.All(files, file =>
            {
                Assert.Equal("unknown-probably-encrypted", file.FileFormat);
                Assert.True(File.Exists(file.AbsolutePath));
            });
        }
        finally
        {
            TryDelete(outDir);
        }
    }
}
