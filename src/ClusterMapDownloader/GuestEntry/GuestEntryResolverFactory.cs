namespace ClusterMapDownloader.GuestEntry;

/// <summary>
/// Central construction point for the opt-in guest-entry resolver. The CLI, the
/// GUI and helper scripts all go through this factory so the reviewable policy
/// (explicit opt-in, single world, in-memory tokens, masked errors) stays
/// uniform across every surface of the project.
///
/// With no arguments at all it resolves the key politely on its own: the
/// package auto-discovery scans the working-tree convention
/// (<c>.work/client-package</c>) and the locally installed cluster client, so
/// "paste an URL and press download" genuinely needs no configuration.
/// </summary>
public static class GuestEntryResolverFactory
{
    /// <summary>Conventional in-repo research dump directory scanned for the key.
    /// Explicit paths are existence-validated so the gate stays deterministic;
    /// surfaces report missing explicit directories with their own message.</summary>
    public static string? ResolveDefaultIl2CppDumpDirectory(string? explicitDirectory)
    {
        if (!string.IsNullOrWhiteSpace(explicitDirectory))
        {
            return Directory.Exists(explicitDirectory) ? explicitDirectory : null;
        }

        var conventional = Path.Combine(Environment.CurrentDirectory, ".work", "il2cpp-dump");
        return Directory.Exists(conventional) ? conventional : null;
    }

    /// <summary>
    /// Builds the guest-entry delivery resolver. Returns null when the caller
    /// provided no key and no client package/dump could be auto-discovered —
    /// the surface (CLI/GUI) then explains the documented options.
    /// </summary>
    public static GuestEntryDeliveryResolver? Create(
        string? firebaseApiKey = null,
        string? clientPackageDirectory = null,
        string? il2CppDumpDirectory = null,
        ClusterApiOptions? api = null,
        HttpMessageHandler? httpHandler = null)
    {
        // The .NET library shares one process-wide, well-defined search list for
        // auto-discovery; a non-Windows GUI/CLI host simply skips the registry.
        var discoveredPackage = ClientPackageLocator.Locate(
            string.IsNullOrWhiteSpace(clientPackageDirectory) ? null : clientPackageDirectory);

        var dumpDirectory = ResolveDefaultIl2CppDumpDirectory(il2CppDumpDirectory);

        if (firebaseApiKey is null && discoveredPackage is null && dumpDirectory is null)
        {
            return null; // nothing to scan; surfaces must report the documented options instead
        }

        return new GuestEntryDeliveryResolver(
            new GuestEntryOptions
            {
                FirebaseApiKey = firebaseApiKey,
                ClientPackageDirectory = discoveredPackage,
                Il2CppDumpDirectory = dumpDirectory,
                Api = api ?? new ClusterApiOptions(),
            },
            httpHandler);
    }
}
