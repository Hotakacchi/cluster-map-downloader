using ClusterMapDownloader;
using Xunit;

namespace ClusterMapDownloader.Tests;

/// <summary>
/// Synthetic fixture data. Deliberately does NOT mirror any real copyrighted
/// world; all identifiers/urls below are fabricated.
/// </summary>
public static class FixtureData
{
    public const string SyntheticWorldId = "11111111-2222-3333-4444-555555555555";

    public const string SyntheticVenueId = "venue_0123456789abcdef01234567";

    public const string SyntheticWorldUrl = $"https://cluster.mu/w/{SyntheticWorldId}";

    public const string SampleWorldMetadataJson = """
        {
          "id": "11111111-2222-3333-4444-555555555555",
          "name": "Example Public World",
          "description": "Synthetic metadata fixture for tests",
          "venueId": "venue_0123456789abcdef01234567",
          "venueSize": 234567890,
          "venueCreatedAt": "2026-01-01T00:00:00.000Z",
          "venueUpdatedAt": "2026-01-02T00:00:00.000Z",
          "venuePublishedAt": "2026-01-02T00:00:00.000Z",
          "worldType": "Normal",
          "privacyType": "Public",
          "sdkType": "ClusterCreatorKit",
          "sdkVersion": "3.3.0",
          "category": "Art",
          "canEntry": true,
          "betaEnabled": false,
          "playCount": 42,
          "likeCount": 7,
          "webUrl": "https://cluster.mu/w/11111111-2222-3333-4444-555555555555",
          "thumbnailUrl": "https://example.invalid/thumbnail.jpg",
          "originalImageUrl": "https://example.invalid/original.jpg",
          "creator": {
            "userId": "00000000-0000-0000-0000-000000000001",
            "username": "example_creator_user",
            "displayName": "Example Creator"
          },
          "someUnknownField": { "x": 1 }
        }
        """;

    /// <summary>Same shape but privacyType Private (must be refused by the pipeline).</summary>
    public const string PrivateWorldMetadataJson = """
        {
          "id": "11111111-2222-3333-4444-555555555555",
          "name": "Example Private World",
          "venueId": "venue_0123456789abcdef01234567",
          "venueSize": 123,
          "privacyType": "Private"
        }
        """;

    public static string Json(string? venueId = null, string? privacyType = null, string? name = null)
    {
        var venueLine = venueId is null ? "null" : $"\"{venueId}\"";
        var privacyLine = privacyType is null ? "null" : $"\"{privacyType}\"";
        var nameLine = name is null ? "null" : $"\"{name}\"";
        return $$"""
            {
              "id": "{{SyntheticWorldId}}",
              "name": {{nameLine}},
              "venueId": {{venueLine}},
              "venueSize": 123,
              "privacyType": {{privacyLine}}
            }
            """;
    }

    public static ClusterApiOptions ApiOptions() => new() { BaseUrl = "https://api.example.invalid/v1" };

    /// <summary>Test harness for <see cref="WorldDownloader"/>.</summary>
    public static WorldDownloadOptions Options(
        string? worldUrl = null,
        bool metadataOnly = false,
        HttpMessageHandler? handler = null,
        ClusterApiOptions? api = null)
        => new()
        {
            WorldUrl = worldUrl ?? SyntheticWorldUrl,
            OutputDirectory = Path.Combine(Path.GetTempPath(), "cmd-tests", Guid.NewGuid().ToString("N")),
            MetadataOnly = metadataOnly,
            DryRun = true,
            Api = api ?? ApiOptions(),
            HttpHandler = handler,
        };
}

/// <summary>Collects notifier messages into a list (no console output).</summary>
public sealed class RecordingNotifier : IProgressNotifier
{
    public sealed record Entry(string Kind, string Message);

    public List<Entry> Entries { get; } = new();

    public void Info(string message) => Entries.Add(new Entry("info", message));
    public void Success(string message) => Entries.Add(new Entry("success", message));
    public void Warn(string message) => Entries.Add(new Entry("warn", message));
    public void Error(string message) => Entries.Add(new Entry("error", message));

    public IEnumerable<string> Messages => Entries.Select(e => e.Message);

    public bool Contains(string fragment) => Entries.Any(e => e.Message.Contains(fragment, StringComparison.Ordinal));
}

/// <summary>Stub HTTP handler with canned responses.</summary>
public sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = new();

    public HttpResponseMessage Respond(HttpRequestMessage request) => responder(request);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(responder(request));
    }
}

public static class HttpResponseFactory
{
    public static StubHttpHandler JsonHandler(string json, int status = 200)
    {
        return new StubHttpHandler(_ =>
        {
            var response = new HttpResponseMessage((System.Net.HttpStatusCode)status)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            };
            return response;
        });
    }

    public static StubHttpHandler StatusHandler(int status)
    {
        return new StubHttpHandler(_ => new HttpResponseMessage((System.Net.HttpStatusCode)status));
    }

    public static StubHttpHandler BufferHandler(byte[] bytes, int status = 200)
    {
        return new StubHttpHandler(_ => new HttpResponseMessage((System.Net.HttpStatusCode)status)
        {
            Content = new ByteArrayContent(bytes),
        });
    }
}
