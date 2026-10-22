using ClusterMapDownloader;
using Xunit;

namespace ClusterMapDownloader.Tests;

public class WorldDownloaderTests
{
    [Fact]
    public async Task Metadata_only_run_reports_success()
    {
        var handler = HttpResponseFactory.JsonHandler(FixtureData.SampleWorldMetadataJson);
        var notifier = new RecordingNotifier();
        var options = FixtureData.Options(metadataOnly: true, handler: handler);

        var result = await WorldDownloader.RunAsync(options, notifier);

        Assert.Equal(WorldDownloadOutcome.Success, result.Outcome);
        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal("Example Public World", result.Metadata!.Name);
        Assert.Equal(FixtureData.SyntheticVenueId, result.Venue!.VenueId);
        Assert.Contains(notifier.Messages, m => m.Contains("Example Public World"));
        Assert.Contains(notifier.Messages, m => m.Contains(FixtureData.SyntheticVenueId));
    }

    [Fact]
    public async Task Full_pipeline_reports_not_implemented_with_exact_message()
    {
        var handler = HttpResponseFactory.JsonHandler(FixtureData.SampleWorldMetadataJson);
        var notifier = new RecordingNotifier();
        var options = FixtureData.Options(handler: handler);

        var result = await WorldDownloader.RunAsync(options, notifier);

        Assert.Equal(WorldDownloadOutcome.NotImplemented, result.Outcome);
        Assert.Equal(ExitCodes.NotImplemented, result.ExitCode);
        Assert.Equal("World asset download is not implemented yet.", result.FailureMessage);
        Assert.True(notifier.Contains("World asset download is not implemented yet."));
        Assert.Contains(notifier.Messages, m => m.Contains(FixtureData.SyntheticVenueId));
    }

    [Fact]
    public async Task Non_public_world_is_refused()
    {
        var handler = HttpResponseFactory.JsonHandler(FixtureData.PrivateWorldMetadataJson);
        var notifier = new RecordingNotifier();
        var options = FixtureData.Options(handler: handler);

        var result = await WorldDownloader.RunAsync(options, notifier);

        Assert.Equal(WorldDownloadOutcome.NotPublic, result.Outcome);
        Assert.Equal(ExitCodes.NotPublic, result.ExitCode);
        Assert.Contains("privacy type is 'Private'", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_venue_stops_the_run()
    {
        var handler = HttpResponseFactory.JsonHandler(FixtureData.Json(venueId: null));
        var notifier = new RecordingNotifier();
        var options = FixtureData.Options(handler: handler);

        var result = await WorldDownloader.RunAsync(options, notifier);

        Assert.Equal(WorldDownloadOutcome.Error, result.Outcome);
        Assert.Contains("venueId", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unknown_world_reports_api_error()
    {
        var handler = HttpResponseFactory.StatusHandler(404);
        var notifier = new RecordingNotifier();
        var options = FixtureData.Options(handler: handler);

        var result = await WorldDownloader.RunAsync(options, notifier);

        Assert.Equal(WorldDownloadOutcome.Error, result.Outcome);
        Assert.Contains("404", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_url_is_usage_error()
    {
        var notifier = new RecordingNotifier();
        var options = FixtureData.Options(worldUrl: "not a url at all");

        var result = await WorldDownloader.RunAsync(options, notifier);

        Assert.Equal(WorldDownloadOutcome.Usage, result.Outcome);
        Assert.Equal(ExitCodes.Usage, result.ExitCode);
    }

    [Fact]
    public async Task Custom_resolver_provides_assets_and_downloader_uses_them()
    {
        // Full-path test against a stub handler serving a small "asset".
        var metadataHandler = HttpResponseFactory.JsonHandler(FixtureData.SampleWorldMetadataJson);
        byte[] assetBytes = "POTATO-ASSET-BYTES"u8.ToArray();
        var assetHandler = HttpResponseFactory.BufferHandler(assetBytes);

        // First call (metadata) goes to the API, subsequent calls (asset) return the bytes.
        var handler = new StubHttpHandler(request => request.RequestUri!.AbsolutePath.StartsWith("/v1/")
            ? metadataHandler.Respond(request)
            : assetHandler.Respond(request));

        var notifier = new RecordingNotifier();
        var options = FixtureData.Options(handler: handler) with
        {
            DryRun = false,
            MetadataOnly = false,
            DeliveryResolver = new StubResolver(),
        };

        var result = await WorldDownloader.RunAsync(options, notifier);

        Assert.Equal(WorldDownloadOutcome.Success, result.Outcome);
        Assert.Single(result.Files);
        Assert.Equal(assetBytes.Length, result.Files[0].Bytes);
        Assert.Contains("bucket.example.invalid", result.Files[0].Url);
        Assert.True(notifier.Contains("Downloaded asset/sample.bin"));
    }

    // ---- World folder naming (map name) tests -----------------------------

    [Fact]
    public void ResolveWorldDirectoryName_uses_sanitized_map_name_when_available()
    {
        var metadata = new WorldMetadata { Id = FixtureData.SyntheticWorldId, Name = "My Cool Map: BEST?" };
        var name = WorldDownloader.ResolveWorldDirectoryName(@"C:\tmp\out", metadata, FixtureData.SyntheticWorldId);
        Assert.Equal("My Cool Map_ BEST_", name);
    }

    [Fact]
    public void ResolveWorldDirectoryName_falls_back_to_world_id_without_a_usable_name()
    {
        foreach (var metadata in new[] { null, new WorldMetadata { Name = "   " }, new WorldMetadata { Name = "\t" } })
        {
            var name = WorldDownloader.ResolveWorldDirectoryName(@"C:\tmp\out", metadata, FixtureData.SyntheticWorldId);
            Assert.Equal(FixtureData.SyntheticWorldId, name);
        }
    }

    [Fact]
    public void ResolveWorldDirectoryName_covers_long_and_control_character_names()
    {
        // The final name is capped at 80 characters and never carries control
        // characters or trailing dots/spaces.
        var metadata = new WorldMetadata { Name = "Map\b Name. " + new string('x', 120) };
        var name = WorldDownloader.ResolveWorldDirectoryName(@"C:\tmp\out", metadata, FixtureData.SyntheticWorldId);
        Assert.DoesNotContain('\b', name);
        Assert.True(name.Length <= 80);
        Assert.False(name.EndsWith('.') || name.EndsWith(' '));

        var controlOnly = WorldDownloader.ResolveWorldDirectoryName(
            @"C:\tmp\out", new WorldMetadata { Name = "\u0001\u0002\u0003" }, FixtureData.SyntheticWorldId);
        Assert.Equal(FixtureData.SyntheticWorldId, controlOnly);
    }

    [Fact]
    public void ResolveWorldDirectoryName_adds_suffix_when_folder_belongs_to_another_world()
    {
        var root = Path.Combine(Path.GetTempPath(), "cmd-tests", Guid.NewGuid().ToString("N"));
        try
        {
            // An existing folder with a DIFFERENT world's metadata snapshot.
            var foreign = Path.Combine(root, "Example Public World");
            Directory.CreateDirectory(foreign);
            File.WriteAllText(
                Path.Combine(foreign, "world-metadata.json"),
                """{"tool":"t","toolVersion":"0","fetchedAtUtc":"2026-01-01T00:00:00Z","sourceUrl":"https://example.invalid","apiEndpoint":"https://example.invalid","metadata":{"id":"99999999-8888-7777-6666-555555555555","name":"Example Public World"}}""",
                new System.Text.UTF8Encoding(false));

            var metadata = new WorldMetadata { Id = FixtureData.SyntheticWorldId, Name = "Example Public World" };
            var name = WorldDownloader.ResolveWorldDirectoryName(root, metadata, FixtureData.SyntheticWorldId);

            // Re-download of the SAME world would reuse the folder; a different
            // world gets a short world-id suffix. The fixture id differs from the
            // snapshot id, so a suffix is required.
            Assert.Equal("Example Public World (11111111)", name);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private sealed class StubResolver : IWorldDeliveryResolver
    {
        public ValueTask<DeliveryResolution> ResolveAsync(DeliveryRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new DeliveryResolution
            {
                Status = DeliveryStatus.Available,
                Reason = "stub",
                Assets = new[]
                {
                    new AssetDownload(
                        new Uri("https://bucket.example.invalid/assets/sample.bin"),
                        "asset/sample.bin",
                        ExpectedSizeBytes: null),
                },
            });
    }
}

public class NotImplementedResolverTests
{
    [Fact]
    public async Task Reports_not_implemented_with_research_steps()
    {
        var metadata = System.Text.Json.JsonSerializer.Deserialize<WorldMetadata>(
            FixtureData.SampleWorldMetadataJson, WorldMetadataJson.Options)!;
        var venue = VenueResolver.Resolve(metadata);

        var resolution = await NotImplementedDeliveryResolver.Instance
            .ResolveAsync(new DeliveryRequest(metadata, venue));

        Assert.Equal(DeliveryStatus.NotImplemented, resolution.Status);
        Assert.NotEmpty(resolution.NextResearchSteps);
        Assert.Contains("RoomVenue.platformResources", resolution.Reason);
        Assert.Contains("401", resolution.Reason);
    }
}
