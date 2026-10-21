using System.Runtime.Versioning;
using Microsoft.Win32;

namespace ClusterMapDownloader.GuestEntry;

/// <summary>
/// Locates the user's local unpacked cluster client package at runtime, so the
/// guest-entry flow can auto-detect the public Firebase web API key without any
/// configuration.
///
/// Policy notes: this only scans the *user's own machine* for the same public
/// client-shipped identifiers the normal client carries (never secrets). It
/// never touches network state, only considers directories that actually
/// contain the known Unity serialized-data files
/// (<c>*_Data/resources.assets</c> or <c>global-metadata.dat</c>), and no
/// candidate is trusted before that verification.
/// </summary>
public static class ClientPackageLocator
{
    /// <summary>
    /// Finds the first directory that looks like a local unpacked cluster
    /// client package (an explicitly user-provided one wins). Returns null
    /// when nothing qualifies — the caller then reports how to proceed.
    /// </summary>
    /// <param name="explicitDirectory">User-provided path; wins over every auto source.</param>
    /// <param name="searchRoots">
    /// Optional roots for tests; when null the standard roots are used
    /// (working-tree <c>.work/client-package</c>, registry-declared cluster
    /// installs, well-known install directories).
    /// </param>
    public static string? Locate(string? explicitDirectory, IReadOnlyList<string>? searchRoots = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitDirectory))
        {
            return LooksLikeClientPackage(explicitDirectory) ? explicitDirectory : null;
        }

        foreach (var root in searchRoots ?? GetDefaultSearchRoots())
        {
            if (LooksLikeClientPackage(root))
            {
                return root;
            }
        }

        return null;
    }

    /// <summary>
    /// Standard roots, in priority order: the developer working-tree
    /// convention first, then an actually installed cluster client on the
    /// user's machine (Windows registry uninstall metadata declared by the
    /// installer, then well-known install directories).
    /// </summary>
    public static IReadOnlyList<string> GetDefaultSearchRoots()
    {
        var roots = new List<string>
        {
            Path.Combine(Environment.CurrentDirectory, ".work", "client-package"),
        };

        if (OperatingSystem.IsWindows())
        {
            roots.AddRange(EnumerateRegistryInstallLocations());
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        roots.Add(Path.Combine(localAppData, "Programs", "cluster"));
        roots.Add(Path.Combine(localAppData, "Programs", "cluster_win"));
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            roots.Add(Path.Combine(programFiles, "cluster"));
            roots.Add(Path.Combine(programFiles, "cluster_win"));
        }

        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            roots.Add(Path.Combine(programFilesX86, "cluster"));
        }

        return roots;
    }

    /// <summary>
    /// A directory counts as a client package when it stores the Unity data
    /// files the key scan consumes (bounded candidate check — no deep walking).
    /// </summary>
    public static bool LooksLikeClientPackage(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }

        if (HasFile(directory, "global-metadata.dat"))
        {
            return true;
        }

        foreach (var dataDir in Directory.EnumerateDirectories(directory, "*_Data", SearchOption.TopDirectoryOnly))
        {
            if (HasFile(dataDir, "resources.assets"))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasFile(string directory, string fileName)
        => Directory.EnumerateFiles(directory, fileName, SearchOption.AllDirectories)
            .Take(1)
            .Any();

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> EnumerateRegistryInstallLocations()
    {
        string[] hives =
        {
            "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall",
            "Software\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall",
        };
        RegistryKey[] bases = { Registry.CurrentUser, Registry.LocalMachine };

        foreach (var hiveBase in bases)
        {
            foreach (var hive in hives)
            {
                using var uninstallKey = hiveBase.OpenSubKey(hive);
                if (uninstallKey is null)
                {
                    continue;
                }

                foreach (var subKeyName in uninstallKey.GetSubKeyNames())
                {
                    string? displayName = null;
                    string? installLocation = null;
                    try
                    {
                        using var subKey = uninstallKey.OpenSubKey(subKeyName);
                        displayName = subKey?.GetValue("DisplayName") as string;
                        installLocation = subKey?.GetValue("InstallLocation") as string;
                    }
                    catch
                    {
                        continue; // unreadable keys are skipped, never fatal
                    }

                    if (string.IsNullOrWhiteSpace(installLocation) || !LooksClusterRelated(displayName, subKeyName))
                    {
                        continue;
                    }

                    yield return installLocation;
                }
            }
        }
    }

    /// <summary>Matches the cluster installer's uninstall entries (e.g. "cluster_win_is1");
    /// every candidate is verified afterwards by <see cref="LooksLikeClientPackage"/>.</summary>
    private static bool LooksClusterRelated(string? displayName, string subKeyName)
    {
        static bool Matches(string value) =>
            value.Contains("cluster", StringComparison.OrdinalIgnoreCase);

        return (displayName is not null && Matches(displayName)) || Matches(subKeyName);
    }
}
