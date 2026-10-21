using System.Text;
using System.Text.RegularExpressions;

namespace ClusterMapDownloader.GuestEntry;

/// <summary>Where the located Firebase web API key came from (for honest reporting; never the value).</summary>
public enum FirebaseApiKeyOrigin
{
    /// <summary>Passed explicitly on the command line.</summary>
    CommandLine,

    /// <summary>Found in the user's local IL2CPP dump/stringliteral research artifacts.</summary>
    LocalResearchDump,

    /// <summary>Found by scanning the user's local cluster client package metadata.</summary>
    LocalClientPackage,
}

/// <summary>
/// Located key. The value stays in memory only; it is never written to disk,
/// logged, or embedded in this repository. <see cref="Masked"/> is safe to print.
/// </summary>
/// <param name="Value">The key itself (secret; in-memory only).</param>
/// <param name="Origin">Where it came from.</param>
/// <param name="Masked">Form safe for logs/console.</param>
public sealed record LocatedFirebaseApiKey(string Value, FirebaseApiKeyOrigin Origin, string Masked);

/// <summary>Raised when no key (or more than one ambiguous key candidate) was found.</summary>
public sealed class FirebaseApiKeyLocatorException(string message) : Exception(message);

/// <summary>
/// Locates the Firebase *web* API key at runtime.
///
/// This key is not a secret: it is a public website/app identifier that the
/// normal cluster client ships to everyone, and it identifies the Firebase
/// project the client's guest sign-in talks to. Still, this repo never embeds
/// the literal. It is obtained from:
/// 1. the CLI (<c>--firebase-api-key</c>, user-supplied), or
/// 2. the user's local research dump (IL2CPP stringliteral.json), or
/// 3. the user's local client package (scan for the AIza-shaped web key).
/// Sources 2-3 read the user's own, locally installed copy of the client —
/// exactly the value the client would use itself.
/// </summary>
public static partial class FirebaseApiKeyLocator
{
    /// <summary>Firebase web API keys are the AIza<39> form (AIza + 35 chars).</summary>
    [GeneratedRegex("AIza[0-9A-Za-z_\\-]{35}")]
    private static partial Regex FirebaseKeyPattern();

    private const int ScanChunkSize = 4 * 1024 * 1024;
    private const int ScanOverlapSize = 128;

    /// <summary>Finds the key. Throws <see cref="FirebaseApiKeyLocatorException"/> with clear instructions on failure.</summary>
    /// <param name="explicitApiKey">CLI-provided value (may be null).</param>
    /// <param name="clientPackageDir">Local client package root (optional).</param>
    /// <param name="il2CppDumpDir">Local IL2CPP dump directory containing stringliteral.json (optional).</param>
    public static LocatedFirebaseApiKey Locate(
        string? explicitApiKey,
        string? clientPackageDir,
        string? il2CppDumpDir)
    {
        if (!string.IsNullOrWhiteSpace(explicitApiKey))
        {
            if (!FirebaseKeyPattern().IsMatch(explicitApiKey))
            {
                throw new FirebaseApiKeyLocatorException(
                    "--firebase-api-key does not look like a Firebase web API key (expected the AIza-key form used by " +
                    "the cluster client). Nothing was sent to the network.");
            }

            return new LocatedFirebaseApiKey(explicitApiKey, FirebaseApiKeyOrigin.CommandLine, Describe(explicitApiKey));
        }

        var found = new List<(string Value, FirebaseApiKeyOrigin Origin)>();

        if (!string.IsNullOrWhiteSpace(il2CppDumpDir))
        {
            var literalPath = Path.Combine(il2CppDumpDir, "stringliteral.json");
            foreach (var value in ScanTextFile(literalPath))
            {
                found.Add((value, FirebaseApiKeyOrigin.LocalResearchDump));
            }
        }

        if (!string.IsNullOrWhiteSpace(clientPackageDir) && found.Count == 0)
        {
            // The observed embedding spot (October 2026 package) is the Unity
            // serialized-data file *_Data/resources.assets; global-metadata.dat
            // does not carry the value. Scan both classes to stay robust across
            // package layouts.
            foreach (var candidateFile in EnumerateCandidatePackageFiles(clientPackageDir))
            {
                foreach (var value in ScanBinaryFile(candidateFile))
                {
                    found.Add((value, FirebaseApiKeyOrigin.LocalClientPackage));
                }
            }
        }

        var distinct = found.Select(f => f.Value).Distinct().ToList();
        if (distinct.Count == 0)
        {
            throw new FirebaseApiKeyLocatorException(BuildNotFoundMessage(clientPackageDir, il2CppDumpDir));
        }

        if (distinct.Count > 1)
        {
            var masked = string.Join(", ", distinct.Select(Describe));
            throw new FirebaseApiKeyLocatorException(
                $"Found {distinct.Count} different AIza-shaped values in the local artifacts " +
                $"({masked}); refusing to guess which one is the cluster Firebase web API key. " +
                "Pass the correct key explicitly with --firebase-api-key.");
        }

        var (key, origin) = found[0];
        return new LocatedFirebaseApiKey(key, origin, Describe(key));
    }

    private static string Describe(string value)
        => SecretMasker.MaskToken(value, value.Length >= 8 ? value[..4] : value);

    /// <summary>
    /// Candidate files inside a local client package, in scan order. The scan
    /// reads streamed, overlapping Latin-1 windows and never emits the value;
    /// name filters keep the scan bounded (package sizes run into hundreds of MB).
    /// </summary>
    private static IEnumerable<string> EnumerateCandidatePackageFiles(string clientPackageDir)
    {
        if (!Directory.Exists(clientPackageDir))
        {
            yield break;
        }

        // 1. IL2CPP metadata (carries the string literals on some layouts).
        foreach (var file in Directory.EnumerateFiles(clientPackageDir, "global-metadata.dat", SearchOption.AllDirectories))
        {
            yield return file;
        }

        // 2. Unity serialized data — observed carrier of the Firebase web API key.
        var unityCandidates = new[] { "resources.assets", "sharedassets*.assets", "*.resource", "*.assets" };
        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dataDir in Directory.EnumerateDirectories(clientPackageDir, "*_Data", SearchOption.AllDirectories))
        {
            foreach (var pattern in unityCandidates)
            {
                foreach (var file in Directory.EnumerateFiles(dataDir, pattern, SearchOption.TopDirectoryOnly))
                {
                    if (yielded.Add(Path.GetFileName(file)))
                    {
                        yield return file;
                    }
                }
            }
        }
    }

    private static string BuildNotFoundMessage(string? clientPackageDir, string? il2CppDumpDir)
    {
        var instructions =
            "The cluster Firebase web API key is a public, non-secret identifier that the normal cluster " +
            "client ships with. This repo never hard-codes it, so one source is required:\n" +
            "  1. pass it explicitly:  cluster-map-downloader download <url> --guest-entry --firebase-api-key <AIza...>\n" +
            "  2. or point --client-package to a local, unpacked cluster client package (the tool scans " +
            "*_Data/resources.assets and global-metadata.dat for the client's own public key), or\n" +
            "  3. or point --il2cpp-dump to a local IL2CPP dump directory containing stringliteral.json." +
            "\nSearched: ";
        var searched = new List<string>();
        if (!string.IsNullOrWhiteSpace(il2CppDumpDir))
        {
            searched.Add($"il2cpp dump '{il2CppDumpDir}'");
        }

        if (!string.IsNullOrWhiteSpace(clientPackageDir))
        {
            searched.Add($"client package '{clientPackageDir}'");
        }

        if (searched.Count == 0)
        {
            searched.Add("nothing (no --client-package/--il2cpp-dump and no --firebase-api-key)");
        }

        return instructions + string.Join("; ", searched) + ".";
    }

    private static List<string> ScanTextFile(string path)
    {
        var values = new List<string>();
        if (!File.Exists(path))
        {
            return values;
        }

        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024 * 512);
        while (reader.ReadLine() is { } line)
        {
            // Values are JSON-escaped; the matched AIza keys themselves are ASCII and unescaped.
            MatchCollection matches;
            try
            {
                matches = FirebaseKeyPattern().Matches(line);
            }
            catch (ArgumentException)
            {
                continue;
            }

            foreach (Match match in matches)
            {
                values.Add(match.Value);
            }
        }

        return values;
    }

    private static List<string> ScanBinaryFile(string path)
    {
        var values = new List<string>();
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > 1024L * 1024 * 1024)
        {
            return values; // sanity guard: absurdly large or empty files are skipped
        }

        using var stream = File.OpenRead(path);
        var latin1 = Encoding.Latin1;
        var buffer = new byte[ScanChunkSize];
        var previousTail = Array.Empty<byte>();
        long offset = 0;

        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                break;
            }

            var chunkText = latin1.GetString(previousTail, 0, previousTail.Length)
                            + latin1.GetString(buffer, 0, read);
            foreach (Match match in FirebaseKeyPattern().Matches(chunkText))
            {
                values.Add(match.Value);
            }

            previousTail = new byte[read < ScanOverlapSize ? read : ScanOverlapSize];
            Array.Copy(buffer, read - previousTail.Length, previousTail, 0, previousTail.Length);
            offset += read;
            if (offset >= long.MaxValue / 2)
            {
                break; // unreachable in practice; guards a pathological stream
            }
        }

        return values;
    }
}
