using System.Security.Cryptography;

namespace ClusterMapDownloader;

/// <summary>
/// Best-effort classification of one downloaded asset file by its leading
/// bytes. Honest labeling only — this project never decrypts anything, so
/// non-plain files are flagged, not transformed.
/// </summary>
public enum AssetFileFormat
{
    /// <summary>Leading bytes match the Unity asset bundle magic ("UnityFS\0").</summary>
    UnityFsPlain,

    /// <summary>Not a recognized plain Unity format; may be a client-encrypted
    /// asset (e.g. AesAssetDecryptor-protected) or another container.</summary>
    EncryptedOrUnknown,
}

public static class AssetFileFormatInspector
{
    private static ReadOnlySpan<byte> UnityFsMagic => "UnityFS\0"u8;

    /// <summary>Machine-readable value used in the manifest (<c>fileFormat</c>).</summary>
    public static string ToManifestValue(AssetFileFormat format)
        => format switch
        {
            AssetFileFormat.UnityFsPlain => "unityfs-plain",
            _ => "unknown-probably-encrypted",
        };

    /// <summary>Human-readable classification for logs/notifiers.</summary>
    public static string Describe(AssetFileFormat format)
        => format switch
        {
            AssetFileFormat.UnityFsPlain => "unityfs-plain (plain Unity asset bundle)",
            _ => "unknown (not UnityFS — possibly client-encrypted; saved as-is, never decrypted)",
        };

    /// <summary>
    /// Reads the first bytes of the file and classifies it. Missing/empty files
    /// classify as <see cref="AssetFileFormat.EncryptedOrUnknown"/> (caller
    /// surfaces decide how to report that).
    /// </summary>
    public static AssetFileFormat Inspect(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            Span<byte> header = stackalloc byte[8];
            var read = 0;
            while (read < header.Length)
            {
                var chunk = stream.Read(header[read..]);
                if (chunk <= 0)
                {
                    return AssetFileFormat.EncryptedOrUnknown;
                }

                read += chunk;
            }

            return header.SequenceEqual(UnityFsMagic)
                ? AssetFileFormat.UnityFsPlain
                : AssetFileFormat.EncryptedOrUnknown;
        }
        catch (IOException)
        {
            return AssetFileFormat.EncryptedOrUnknown;
        }
    }

    /// <summary>SHA-256 of the file contents, lowercase hex (manifest integrity column).</summary>
    public static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
