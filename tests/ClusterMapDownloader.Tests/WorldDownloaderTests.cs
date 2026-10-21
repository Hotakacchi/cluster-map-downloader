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
