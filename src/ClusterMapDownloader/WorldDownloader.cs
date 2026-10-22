using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClusterMapDownloader;

/// <summary>
/// Orchestrates world download pipeline stages:
/// 1. parse world URL, 2. fetch public metadata, 3. resolve venue id,
/// 4. resolve the normal client-side delivery mechanism,
/// 5. download publicly accessible assets (when the delivery resolver supports it),
/// 6. write manifests/metadata snapshots and report.
/// </summary>
public static class WorldDownloader
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<WorldDownloadResult> RunAsync(
        WorldDownloadOptions options,
        IProgressNotifier notifier,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await RunCoreAsync(options, notifier, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            notifier.Error("Operation cancelled.");
            return Fail(options, WorldDownloadOutcome.Error, "Operation cancelled.", Array.Empty<string>());
        }
        catch (Exception ex) when (ex is not ClusterApiException)
        {
            notifier.Error($"Unexpected failure: {ex.Message}");
            return Fail(options, WorldDownloadOutcome.Error, $"Unexpected failure: {ex.Message}",
                Array.Empty<string>());
        }
    }

    private static async Task<WorldDownloadResult> RunCoreAsync(
        WorldDownloadOptions options,
        IProgressNotifier notifier,
        CancellationToken cancellationToken)
    {
        notifier.Info($"{ToolInfo.Name} v{ToolInfo.Version} — {ToolInfo.UnofficialNotice}");
        notifier.Info("This tool fetches only publicly accessible data and implements no bypass of any kind.");

        // ---- Stage 1: parse the world URL ---------------------------------
        notifier.Info("[1/6] Parsing world URL ...");
        if (!WorldUrl.TryParse(options.WorldUrl, out var worldUrl, out var parseError))
        {
            notifier.Error(parseError ?? "Could not parse the world URL.");
            return Fail(options, WorldDownloadOutcome.Usage,
                parseError ?? "Could not parse the world URL.", Array.Empty<string>());
        }

        var warnings = new List<string>();
        if (!worldUrl.IsGuidLike)
        {
            var note = $"World id '{worldUrl.WorldId}' does not look like the commonly observed GUID form.";
            notifier.Warn(note + " Continuing anyway.");
            warnings.Add(note);
        }

        notifier.Success($"Parsed world id: {worldUrl.WorldId} ({worldUrl.CanonicalUrl})");

        // ---- Stage 2: fetch public metadata -------------------------------
        notifier.Info($"[2/6] Fetching public world metadata from {DescribeMetadataEndpoint(options)} ...");
        WorldMetadata metadata;
        try
        {
            await using var client = new ClusterApiClient(options.Api, options.HttpHandler);
            metadata = await client.GetWorldMetadataAsync(worldUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (ClusterApiException ex)
        {
            notifier.Error(ex.Message);
            return Fail(options, WorldDownloadOutcome.Error, ex.Message, warnings, worldId: worldUrl.WorldId);
        }

        notifier.Success($"Fetched metadata for '{metadata.Name ?? metadata.Id ?? "?"}'.");

        // ---- Stage 2.5: resolve the world folder --------------------------
        // Directory name = the public map name when the API provides a usable
        // one (sanitized; falls back to the world id so unknown/blank names
        // behave exactly as before). Collisions with another world's folder
        // are disambiguated with a short world-id suffix, then numbers.
        var worldDir = Path.Combine(options.OutputDirectory,
            ResolveWorldDirectoryName(options.OutputDirectory, metadata, worldUrl.WorldId));
        if (!options.DryRun)
        {
            Directory.CreateDirectory(worldDir);
        }

        // ---- Stage 3: report metadata & resolve venue id -------------------
        notifier.Info("[3/6] World metadata & venue resolution ...");
        PrintMetadata(metadata, notifier);

        var venue = VenueResolver.Resolve(metadata);
        foreach (var w in venue.Warnings)
        {
            notifier.Warn(w);
            warnings.Add(w);
        }

        if (!venue.Found)
        {
            const string message =
                "No venueId in metadata: this world cannot be resolved to an internal venue identifier, " +
                "so asset delivery cannot be attempted.";
            notifier.Error(message);
            return Fail(options, WorldDownloadOutcome.Error, message, warnings, metadata, venue, worldUrl.WorldId);
        }

        notifier.Success($"Venue id: {venue.VenueId} (format looks valid: {venue.FormatLooksValid})");

        // ---- Persist a metadata snapshot (public data) ----------------------
        if (!options.DryRun)
        {
            WriteJson(Path.Combine(worldDir, "world-metadata.json"),
                new MetadataSnapshot
                {
                    Tool = ToolInfo.Name,
                    ToolVersion = ToolInfo.Version,
                    FetchedAtUtc = DateTimeOffset.UtcNow,
                    SourceUrl = worldUrl.CanonicalUrl,
                    ApiEndpoint = DescribeMetadataEndpoint(options),
                    Metadata = metadata,
                },
                notifier);
        }

        // ---- metadata-only short circuit ------------------------------------
        if (options.MetadataOnly)
        {
            notifier.Success("Metadata-only mode: stopping before delivery resolution as requested.");
            return new WorldDownloadResult
            {
                Outcome = WorldDownloadOutcome.Success,
                Metadata = metadata,
                Venue = venue,
                Options = options,
                WorldId = worldUrl.WorldId,
                Warnings = warnings,
            };
        }

        // ---- Stage 4: privacy guard -----------------------------------------
        notifier.Info("[4/6] Checking world public accessibility ...");
        if (metadata.PrivacyType is { } privacy && !privacy.Equals("Public", StringComparison.OrdinalIgnoreCase))
        {
            var message = $"World privacy type is '{privacy}', not 'Public'. This tool only supports publicly " +
                          "accessible worlds and will not attempt to bypass access controls.";
            notifier.Error(message);
            return Fail(options, WorldDownloadOutcome.NotPublic, message, warnings, metadata, venue,
                worldUrl.WorldId);
        }

        notifier.Success("World is publicly accessible (privacyType 'Public' or not reported).");

        // ---- Stage 5: resolve normal client delivery mechanism ---------------
        notifier.Info("[5/6] Resolving the normal client-side delivery mechanism ...");
        var resolver = options.DeliveryResolver ?? NotImplementedDeliveryResolver.Instance;
        var delivery = await resolver.ResolveAsync(new DeliveryRequest(metadata, venue), cancellationToken)
            .ConfigureAwait(false);

        switch (delivery.Status)
        {
            case DeliveryStatus.NotImplemented:
            {
                notifier.Warn($"Delivery mechanism not implemented: {delivery.Reason}");
                foreach (var step in delivery.NextResearchSteps)
                {
                    notifier.Info($"  next step: {step}");
                }

                const string notImplemented = "World asset download is not implemented yet.";
                notifier.Error(notImplemented);
                return new WorldDownloadResult
                {
                    Outcome = WorldDownloadOutcome.NotImplemented,
                    FailureMessage = notImplemented,
                    Metadata = metadata,
                    Venue = venue,
                    Delivery = delivery,
                    Options = options,
                    WorldId = worldUrl.WorldId,
                    Warnings = warnings,
                };
            }

            case DeliveryStatus.RequiresNonPublicAccess:
            {
                const string message = "Downloading requires authorization that the normal public flow does not " +
                                       "provide; stopping here as configured by project policy.";
                notifier.Error(message);
                notifier.Info($"  reason: {delivery.Reason}");
                return new WorldDownloadResult
                {
                    Outcome = WorldDownloadOutcome.RequiresNonPublicAccess,
                    FailureMessage = message,
                    Metadata = metadata,
                    Venue = venue,
                    Delivery = delivery,
                    Options = options,
                    WorldId = worldUrl.WorldId,
                    Warnings = warnings,
                };
            }
        }

        // ---- Stage 6: download publicly accessible assets ---------------------
        notifier.Info($"[6/6] Downloading {delivery.Assets.Count} asset file(s) ...");
        if (options.DryRun)
        {
            foreach (var asset in delivery.Assets)
            {
                notifier.Info($"  dry-run: would download {asset.RelativeFileName} from {asset.Url}");
            }

            notifier.Success("Dry-run complete: no files were written.");
            return new WorldDownloadResult
            {
                Outcome = WorldDownloadOutcome.Success,
                Metadata = metadata,
                Venue = venue,
                Delivery = delivery,
                Options = options,
                WorldId = worldUrl.WorldId,
                Warnings = warnings,
            };
        }

        IReadOnlyList<DownloadedFile> files;
        try
        {
            files = await FileDownloader.DownloadAllAsync(
                options.HttpHandler ?? new HttpClientHandler(),
                delivery.Assets,
                worldDir,
                notifier,
                cancellationToken,
                options.FileProgress).ConfigureAwait(false);
        }
        catch (ClusterApiException ex)
        {
            notifier.Error(ex.Message);
            return Fail(options, WorldDownloadOutcome.Error, ex.Message, warnings, metadata, venue,
                worldUrl.WorldId, delivery);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            notifier.Error($"Asset download failed: {ex.Message}");
            return Fail(options, WorldDownloadOutcome.Error, $"Asset download failed: {ex.Message}", warnings,
                metadata, venue, worldUrl.WorldId, delivery);
        }

        foreach (var w in warnings)
        {
            notifier.Warn($"warning: {w}");
        }

        notifier.Success($"Saved {files.Count} file(s) ({FileDownloader.FormatBytes(files.Sum(f => f.Bytes))}) " +
                         $"into {worldDir}.");

        WriteJson(Path.Combine(worldDir, "downloads-manifest.json"),
            new DownloadsManifest
            {
                Tool = ToolInfo.Name,
                ToolVersion = ToolInfo.Version,
                WrittenAtUtc = DateTimeOffset.UtcNow,
                WorldUrl = worldUrl.CanonicalUrl,
                WorldId = worldUrl.WorldId,
                VenueId = venue.VenueId,
                Files = files,
            },
            notifier);

        return new WorldDownloadResult
        {
            Outcome = WorldDownloadOutcome.Success,
            Metadata = metadata,
            Venue = venue,
            Delivery = delivery,
            Files = files,
            Options = options,
            WorldId = worldUrl.WorldId,
            Warnings = warnings,
        };
    }

    private static string DescribeMetadataEndpoint(WorldDownloadOptions options)
        => $"{options.Api.BaseUrl.TrimEnd('/')}/worlds/{{worldId}}";

    private static void PrintMetadata(WorldMetadata metadata, IProgressNotifier notifier)
    {
        void Line(string label, string? value)
            => notifier.Info($"        {label,-15}: {(string.IsNullOrWhiteSpace(value) ? "-" : value!.Trim())}");

        Line("id", metadata.Id);
        Line("name", metadata.Name);
        Line("worldType", metadata.WorldType);
        Line("privacyType", metadata.PrivacyType);
        Line("category", metadata.Category);
        Line("sdkType", metadata.SdkVersion is { } sdkVersion
            ? $"{metadata.SdkType} {sdkVersion}"
            : metadata.SdkType);
        Line("venueId", metadata.VenueId);
        Line("venueSize", metadata.VenueSize is { } size ? $"{size} bytes ({FileDownloader.FormatBytes(size)})" : null);
        Line("venueUpdatedAt", metadata.VenueUpdatedAt);
        Line("creator", metadata.Creator is null
            ? null
            : $"{metadata.Creator.DisplayName ?? "-"} ({metadata.Creator.Username ?? "-"})");
        Line("canEntry", metadata.CanEntry?.ToString());
        Line("webUrl", metadata.WebUrl);
    }

    private static WorldDownloadResult Fail(
        WorldDownloadOptions options,
        WorldDownloadOutcome outcome,
        string message,
        IReadOnlyList<string> warnings,
        WorldMetadata? metadata = null,
        VenueResolution? venue = null,
        string? worldId = null,
        DeliveryResolution? delivery = null)
        => new()
        {
            Outcome = outcome,
            FailureMessage = message,
            Warnings = warnings,
            Metadata = metadata,
            Venue = venue,
            Delivery = delivery,
            Options = options,
            WorldId = worldId,
        };

    private sealed record MetadataSnapshot
    {
        [JsonPropertyName("tool")]
        public string? Tool { get; init; }

        [JsonPropertyName("toolVersion")]
        public string? ToolVersion { get; init; }

        [JsonPropertyName("fetchedAtUtc")]
        public DateTimeOffset FetchedAtUtc { get; init; }

        [JsonPropertyName("sourceUrl")]
        public string? SourceUrl { get; init; }

        [JsonPropertyName("apiEndpoint")]
        public string? ApiEndpoint { get; init; }

        [JsonPropertyName("metadata")]
        public WorldMetadata? Metadata { get; init; }
    }

    private sealed record DownloadsManifest
    {
        [JsonPropertyName("tool")]
        public string? Tool { get; init; }

        [JsonPropertyName("toolVersion")]
        public string? ToolVersion { get; init; }

        [JsonPropertyName("writtenAtUtc")]
        public DateTimeOffset WrittenAtUtc { get; init; }

        [JsonPropertyName("worldUrl")]
        public string? WorldUrl { get; init; }

        [JsonPropertyName("worldId")]
        public string? WorldId { get; init; }

        [JsonPropertyName("venueId")]
        public string? VenueId { get; init; }

        [JsonPropertyName("files")]
        public IReadOnlyList<DownloadedFile> Files { get; init; } = Array.Empty<DownloadedFile>();
    }

    /// <summary>
    /// On-disk world directory name: the public map name (sanitized) when the
    /// API provides a usable one, the raw world id otherwise (unchanged legacy
    /// behavior for name-less metadata). A folder that already exists for a
    /// DIFFERENT world gets a short world-id suffix, then a number.
    /// </summary>
    public static string ResolveWorldDirectoryName(
        string outputRoot,
        WorldMetadata? metadata,
        string worldId)
    {
        var name = SanitizeDirectoryComponent(metadata?.Name);
        if (string.IsNullOrEmpty(name) ||
            string.Equals(name, worldId, StringComparison.OrdinalIgnoreCase))
        {
            return worldId;
        }

        var candidateDir = Path.Combine(outputRoot, name);
        if (!Directory.Exists(candidateDir))
        {
            return name;
        }

        // Re-download of the same world: reuse its existing folder.
        if (SnapshotDeclaresWorldId(candidateDir, worldId))
        {
            return name;
        }

        var shortId = worldId.Length > 8 ? worldId[..8] : worldId;
        var withId = $"{name} ({shortId})";
        if (!Directory.Exists(Path.Combine(outputRoot, withId)))
        {
            return withId;
        }

        for (var i = 2; i < 1000; i++)
        {
            var numbered = $"{name} ({i})";
            if (!Directory.Exists(Path.Combine(outputRoot, numbered)))
            {
                return numbered;
            }
        }

        return withId;
    }

    private static string SanitizeDirectoryComponent(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder();
        foreach (var ch in raw.Trim())
        {
            if (char.IsControl(ch))
            {
                continue;
            }

            builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        }

        var text = builder.ToString().TrimEnd(' ', '.');
        if (text.Length > 80)
        {
            text = text[..80];
        }

        return text;
    }

    private static bool SnapshotDeclaresWorldId(string worldDir, string worldId)
    {
        try
        {
            var snapshotPath = Path.Combine(worldDir, "world-metadata.json");
            if (!File.Exists(snapshotPath))
            {
                return false;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(snapshotPath));
            if (!document.RootElement.TryGetProperty("metadata", out var metadataElement))
            {
                return false;
            }

            return metadataElement.TryGetProperty("id", out var idElement) &&
                   idElement.ValueEquals(worldId);
        }
        catch
        {
            return false;
        }
    }

    private static void WriteJson<T>(string path, T value, IProgressNotifier notifier)
    {        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(value, ManifestJsonOptions));
            notifier.Info($"Wrote {path}");
        }
        catch (Exception ex)
        {
            notifier.Warn($"Could not write {path}: {ex.Message}");
        }
    }
}
