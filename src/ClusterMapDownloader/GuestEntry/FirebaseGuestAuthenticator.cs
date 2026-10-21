using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClusterMapDownloader.GuestEntry;

/// <summary>In-memory-only Firebase credential (idToken/refreshToken). Never persisted or logged.</summary>
public sealed record FirebaseCredential(
    string IdToken,
    string? RefreshToken,
    string? ExpiresInSeconds,
    int? ExpiresInSecondsParsed);

/// <summary>Firebase REST authentication failure (parsed from the error envelope).</summary>
public sealed class FirebaseAuthException : Exception
{
    public FirebaseAuthException(string message, int statusCode) : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>HTTP status of the failed Firebase call.</summary>
    public int StatusCode { get; }
}

/// <summary>
/// Reproduces the *normal public client* Firebase guest sign-in over the public
/// identitytoolkit REST endpoints. The desktop client does exactly this (see
/// ClusterVR.Infrastructure.Firebase.Authenticator.FirebaseAuthenticator in the
/// IL2CPP dump): a fresh install performs an anonymous sign-up and then sends the
/// resulting idToken as an Authorization header.
///
/// This class exists only as the documented opt-in path (--guest-entry). It
/// performs one honest REST round-trip with the public web API key, keeps all
/// tokens in memory, and masks any value that could contain a credential.
/// </summary>
public sealed class FirebaseGuestAuthenticator(HttpMessageHandler? handler = null) : IDisposable
{
    // Exactly the URLs from FirebaseAuthenticator in the client dump:
    //   GoogleSignUpUrl: https://www.googleapis.com/identitytoolkit/v3/relyingparty/signupNewUser?key={0}
    private const string SignUpUrlTemplate =
        "https://www.googleapis.com/identitytoolkit/v3/relyingparty/signupNewUser?key={0}";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http = handler is null
        ? new HttpClient()
        : new HttpClient(handler, disposeHandler: false);

    /// <summary>
    /// Anonymous Firebase sign-up (`signupNewUser`), as the fresh-install guest
    /// client does. Returns the credential; nothing is written to disk.
    /// </summary>
    public async Task<FirebaseCredential> SignInAnonymouslyAsync(string webApiKey, CancellationToken cancellationToken = default)
    {
        var requestUrl = string.Format(SignUpUrlTemplate, Uri.EscapeDataString(webApiKey));
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUrl)
        {
            Content = new StringContent("""{"returnSecureToken":true}""", Encoding.UTF8, "application/json"),
        };

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new FirebaseAuthException(
                $"Network error during the guest sign-in round-trip: {SecretMasker.Mask(ex.Message)}", 0);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new FirebaseAuthException("Guest sign-in request timed out.", 0);
        }

        using (response)
        {
            var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw ParseError(response.StatusCode, raw);
            }

            SignUpResponse? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<SignUpResponse>(raw, JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new FirebaseAuthException($"Could not parse the guest sign-in response: {ex.Message}", 0);
            }

            if (parsed is null || string.IsNullOrWhiteSpace(parsed.IdToken))
            {
                throw new FirebaseAuthException("Guest sign-in response did not contain an idToken.", 0);
            }

            return new FirebaseCredential(
                parsed.IdToken,
                parsed.RefreshToken,
                parsed.ExpiresIn,
                long.TryParse(parsed.ExpiresIn, out var seconds) ? (int?)(int)Math.Min(seconds, int.MaxValue) : null);
        }
    }

    private static FirebaseAuthException ParseError(System.Net.HttpStatusCode statusCode, string body)
    {
        var message = SecretMasker.Mask(body);
        string? code = null;
        try
        {
            var error = JsonSerializer.Deserialize<ErrorResponse>(body, JsonOptions)?.Error;
            if (error is not null)
            {
                code = error.Message;
            }
        }
        catch (JsonException)
        {
            // fall through with the whole body text (already masked)
        }

        var description = string.IsNullOrWhiteSpace(code) ? message : $"{code} — {message}";
        return new FirebaseAuthException(
            $"Guest sign-in refused (HTTP {(int)statusCode}): {description}", (int)statusCode);
    }

    private sealed record SignUpResponse
    {
        [JsonPropertyName("idToken")]
        public string? IdToken { get; init; }

        [JsonPropertyName("refreshToken")]
        public string? RefreshToken { get; init; }

        [JsonPropertyName("expiresIn")]
        public string? ExpiresIn { get; init; }

        [JsonPropertyName("localId")]
        public string? LocalId { get; init; }
    }

    private sealed record ErrorResponse
    {
        [JsonPropertyName("error")]
        public ErrorBody? Error { get; init; }
    }

    private sealed record ErrorBody
    {
        [JsonPropertyName("message")]
        public string? Message { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }
    }

    public void Dispose() => _http.Dispose();
}
