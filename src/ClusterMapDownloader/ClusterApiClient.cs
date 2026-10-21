using System.Net.Http.Json;
using System.Text.Json;

namespace ClusterMapDownloader;

/// <summary>An error raised while talking to or parsing the public cluster web API.</summary>
public sealed class ClusterApiException(string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>HTTP status code, when the failure came from an HTTP response.</summary>
    public int? StatusCode { get; init; }
}

/// <summary>
/// Minimal client for the public cluster web API used by this tool.
/// Currently only world metadata is needed; asset delivery endpoints are
/// unknown and intentionally not guessed.
/// </summary>
public sealed class ClusterApiClient : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly ClusterApiOptions _options;

    /// <summary>Pass a custom <paramref name="handler"/> for tests; otherwise the client owns its handler.</summary>
    public ClusterApiClient(ClusterApiOptions? options = null, HttpMessageHandler? handler = null)
    {
        _options = options ?? new ClusterApiOptions();
        // A base address is deliberately not set so a malformed BaseUrl fails loudly.
        _http = handler is null
            ? new HttpClient()
            : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(60);
        foreach (var (name, value) in _options.BuildHeaders())
        {
            _http.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
        }
    }

    /// <summary>Builds the metadata request URL for a world, e.g. https://api.cluster.mu/v1/worlds/{id}.</summary>
    public Uri BuildWorldMetadataUrl(WorldUrl world)
    {
        var baseTrimmed = _options.BaseUrl.TrimEnd('/');
        return new Uri($"{baseTrimmed}/worlds/{world.WorldId}");
    }

    /// <summary>
    /// Fetches public world metadata. Throws <see cref="ClusterApiException"/>
    /// (with <see cref="ClusterApiException.StatusCode"/>) on HTTP failures,
    /// including a specific 404 "world not found" message.
    /// </summary>
    public async Task<WorldMetadata> GetWorldMetadataAsync(WorldUrl world, CancellationToken cancellationToken = default)
    {
        var url = BuildWorldMetadataUrl(world);
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ClusterApiException(
                $"Network error while requesting public world metadata from {url}: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ClusterApiException($"Request to {url} timed out.", ex);
        }

        using (response)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                throw GetWorldNotFoundException(world.WorldId, response);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new ClusterApiException(
                    $"Public world metadata API returned HTTP {(int)response.StatusCode} ({response.StatusCode}) " +
                    $"for world id '{world.WorldId}' at {url}.")
                {
                    StatusCode = (int)response.StatusCode,
                };
            }

            WorldMetadata? metadata;
            try
            {
                metadata = await response.Content
                    .ReadFromJsonAsync<WorldMetadata>(WorldMetadataJson.Options, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                throw new ClusterApiException($"Failed to parse world metadata JSON from {url}: {ex.Message}", ex);
            }

            return metadata ?? throw new ClusterApiException($"Metadata response from {url} was empty.");
        }
    }

    private static ClusterApiException GetWorldNotFoundException(string worldId, HttpResponseMessage response)
        => new(
            $"World not found: the public metadata API returned HTTP 404 for world id '{worldId}'. " +
            "Either the world id is wrong or the world page is not publicly accessible.")
        {
            StatusCode = 404,
        };

    public void Dispose() => _http.Dispose();

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
