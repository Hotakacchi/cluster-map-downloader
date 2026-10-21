using System.Net;
using ClusterMapDownloader;
using Xunit;

namespace ClusterMapDownloader.Tests;

public class ClusterApiClientTests
{
    [Fact]
    public async Task Sends_observed_web_client_headers()
    {
        var handler = HttpResponseFactory.JsonHandler(FixtureData.SampleWorldMetadataJson);
        var options = FixtureData.ApiOptions();

        await using var client = new ClusterApiClient(options, handler);
        var world = WorldUrl.Parse(FixtureData.SyntheticWorldUrl);
        await client.GetWorldMetadataAsync(world);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(new Uri($"https://api.example.invalid/v1/worlds/{FixtureData.SyntheticWorldId}"),
            request.RequestUri);

        foreach (var (name, value) in options.BuildHeaders())
        {
            Assert.True(request.Headers.TryGetValues(name, out var values),
                $"header {name} missing from request");
            Assert.Equal(value, Assert.Single(values!));
        }
    }

    [Fact]
    public async Task Parses_metadata_from_response()
    {
        var handler = HttpResponseFactory.JsonHandler(FixtureData.SampleWorldMetadataJson);
        await using var client = new ClusterApiClient(FixtureData.ApiOptions(), handler);
        var metadata = await client.GetWorldMetadataAsync(WorldUrl.Parse(FixtureData.SyntheticWorldUrl));
        Assert.Equal("Example Public World", metadata.Name);
        Assert.Equal(FixtureData.SyntheticVenueId, metadata.VenueId);
    }

    [Fact]
    public async Task NotFound_produces_clear_exception()
    {
        var handler = HttpResponseFactory.StatusHandler(404);
        await using var client = new ClusterApiClient(FixtureData.ApiOptions(), handler);

        var ex = await Assert.ThrowsAsync<ClusterApiException>(()
            => client.GetWorldMetadataAsync(WorldUrl.Parse(FixtureData.SyntheticWorldUrl)));

        Assert.Equal(404, ex.StatusCode);
        Assert.Contains("HTTP 404", ex.Message);
        Assert.Contains(FixtureData.SyntheticWorldId, ex.Message);
    }

    [Fact]
    public async Task Generic_http_error_includes_status()
    {
        var handler = HttpResponseFactory.StatusHandler(503);
        await using var client = new ClusterApiClient(FixtureData.ApiOptions(), handler);

        var ex = await Assert.ThrowsAsync<ClusterApiException>(()
            => client.GetWorldMetadataAsync(WorldUrl.Parse(FixtureData.SyntheticWorldUrl)));

        Assert.Equal(503, ex.StatusCode);
        Assert.Contains("503", ex.Message);
    }
}

public class ClusterApiOptionsTests
{
    [Fact]
    public void Defaults_match_observed_web_client_headers()
    {
        var options = new ClusterApiOptions();
        var headers = options.BuildHeaders();

        Assert.Equal(ClusterApiOptions.DefaultBaseUrl, options.BaseUrl);
        Assert.Equal("3.103.2610071832", headers["X-Cluster-App-Version"]);
        Assert.Equal("2610141027", headers["X-Cluster-Build-Version"]);
        Assert.Equal("Web", headers["X-Cluster-Device"]);
        Assert.Equal("Web", headers["X-Cluster-Platform"]);
        Assert.Contains("cluster-map-downloader", headers["User-Agent"]);
    }

    [Fact]
    public void Overrides_apply_to_headers()
    {
        var options = new ClusterApiOptions { AppVersion = "1.2.3", BuildVersion = "42", Platform = "Windows" };
        var headers = options.BuildHeaders();
        Assert.Equal("1.2.3", headers["X-Cluster-App-Version"]);
        Assert.Equal("42", headers["X-Cluster-Build-Version"]);
        Assert.Equal("Windows", headers["X-Cluster-Platform"]);
    }
}
