using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClusterMapDownloader.GuestEntry;

/// <summary>
/// Small client for the room-enter part of cluster's web API as used by the
/// normal guest flow (room is created via the world's entry request, then the
/// room-enter response delivers the venue asset manifest). Requires a Firebase
/// idToken obtained by <see cref="FirebaseGuestAuthenticator"/>; tokens are
/// attached per request and never stored or printed.
/// </summary>
public sealed class RoomEntryApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly ClusterApiOptions _options;
    private bool _disposed;

    /// <summary>Pass a custom <paramref name="handler"/> for tests; otherwise an owned handler is used.</summary>
    public RoomEntryApiClient(ClusterApiOptions? options = null, HttpMessageHandler? handler = null)
    {
        _options = options ?? new ClusterApiOptions();
        _http = handler is null
            ? new HttpClient()
            : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(60);
        foreach (var (name, value) in _options.BuildHeaders())
        {
            _http.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
        }
    }

    /// <summary>
    /// Cluster guest login (POST /v1/login) exactly as the client's
    /// ApiUserRepository.LoginAsync → RimuldarApiClient.LoginAsync does right
    /// after the anonymous Firebase sign-up: Bearer idToken, empty JSON object
    /// body, response = UserPrivateResponse (isAnonymous, userId, ...).
    /// Without this call the API refuses every room route with 401
    /// BAD_TOKEN_EXIST (verified live, October 2026).
    /// </summary>
    public Task<GuestLoginResponse> LoginAsync(string idToken, CancellationToken cancellationToken = default)
    {
        var url = BuildUrl("login");
        return PostAndParseAsync<GuestLoginResponse>(url, idToken, "{}", cancellationToken);
    }

    /// <summary>
    /// Sets the X-Cluster-LoginID header (RimuldarApiHeaderProcessor.
    /// ClusterLoginIdKey) from the /v1/login response; room routes are
    /// evaluated per logged-in user (live-verified: missing header ⇒
    /// 400 BAD_REQUEST_ROOM_ENTER "room entry request not exist").
    /// </summary>
    public void SetLoginId(string userId)
    {
        _http.DefaultRequestHeaders.Remove("X-Cluster-LoginID");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Cluster-LoginID", userId);
    }

    /// <summary>
    /// Creates the runtime room for a world (POST /v1/world_room_set/{wrsId}/entry_request),
    /// as the client does before entering (CreateEntryRequestService →
    /// CreateWorldRoomSetEntryRequestAsync in the IL2CPP dump). Returns the runtime roomId.
    /// </summary>
    public async Task<CreateEntryRequestResponse> CreateWorldRoomSetEntryRequestAsync(
        string worldRoomSetId, string idToken, CancellationToken cancellationToken = default)
    {
        var url = BuildUrl($"world_room_set/{Uri.EscapeDataString(worldRoomSetId)}/entry_request");
        return await PostAndParseAsync<CreateEntryRequestResponse>(url, idToken, string.Empty, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Room enter v2 (POST /v1/rooms/{roomId}/enter_v2) with the same payload
    /// shape the client sends (PostEnterV2RoomPayload: one "metricsPayload"
    /// string). The response contains RoomVenue.platformResources — the venue
    /// asset manifest.
    /// </summary>
    public async Task<RoomEnterResponse> EnterRoomV2Async(
        string roomId, string metricsPayloadJson, string idToken, CancellationToken cancellationToken = default)
    {
        var url = BuildUrl($"rooms/{Uri.EscapeDataString(roomId)}/enter_v2");
        return await PostAndParseAsync<RoomEnterResponse>(
            url, idToken, JsonSerializer.Serialize(new MetricsPayload(metricsPayloadJson)), cancellationToken)
            .ConfigureAwait(false);
    }

    private Uri BuildUrl(string relative)
        => new($"{_options.BaseUrl.TrimEnd('/')}/{relative}");

    private async Task<T> PostAndParseAsync<T>(Uri url, string idToken, string jsonBody,
        CancellationToken cancellationToken)
    {
        using var request = BuildAuthorizedRequest(HttpMethod.Post, url, idToken);
        request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync<T>(response, url, cancellationToken).ConfigureAwait(false);
    }

    private static HttpRequestMessage BuildAuthorizedRequest(HttpMethod method, Uri url, string idToken)
    {
        var request = new HttpRequestMessage(method, url);
        // Same Authorization shape the client uses (RimuldarApiHeaderProcessor.BuildBearerToken).
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {idToken}");
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ClusterApiException(
                $"Network error while requesting {request.RequestUri}: {SecretMasker.Mask(ex.Message)}", ex)
            {
                StatusCode = null,
            };
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ClusterApiException($"Request to {request.RequestUri} timed out.");
        }

        return response;
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, Uri url,
        CancellationToken cancellationToken)
    {
        var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var detail = TryDescribeApiError(raw);
            throw new ClusterApiException(
                $"cluster API returned HTTP {(int)response.StatusCode} for {url}: {detail}")
            {
                StatusCode = (int)response.StatusCode,
            };
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<T>(raw, WorldMetadataJson.Options);
            return parsed ?? throw new ClusterApiException($"Response from {url} was empty.");
        }
        catch (JsonException ex)
        {
            throw new ClusterApiException($"Failed to parse response JSON from {url}: {ex.Message}", ex);
        }
    }

    /// <summary>cluster error envelope: {"id","code","status","detail","meta"}; masked before display.</summary>
    private static string TryDescribeApiError(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var code = root.TryGetProperty("code", out var codeElement) ? codeElement.GetString() : null;
            var detail = root.TryGetProperty("detail", out var detailElement) ? detailElement.GetString() : null;
            var status = root.TryGetProperty("status", out var statusElement) ? statusElement.ToString() : null;

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(code))
            {
                parts.Add($"code={code}");
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                parts.Add($"status={status}");
            }

            if (!string.IsNullOrWhiteSpace(detail))
            {
                parts.Add($"detail={SecretMasker.Mask(detail)}");
            }

            if (parts.Count == 0)
            {
                parts.Add(SecretMasker.Mask(raw));
            }

            return string.Join(", ", parts);
        }
        catch (JsonException)
        {
            return SecretMasker.Mask(raw);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _http.Dispose();
    }

    /// <summary>Payload body of enter_v2 exactly as the client sends it (one metricsPayload string).</summary>
    private sealed record MetricsPayload(
        [property: JsonPropertyName("metricsPayload")] string MetricsPayloadJson);
}
