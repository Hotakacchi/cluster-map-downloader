using System.Text.RegularExpressions;

namespace ClusterMapDownloader.GuestEntry;

/// <summary>Thrown by the guest flow when cluster refuses the normal guest path; carries the honest resolution.</summary>
public sealed class GuestEntryRefusalException(string refusalMessage, ClusterApiException cause)
    : Exception(refusalMessage, cause)
{
    public DeliveryResolution Resolution { get; } = new()
    {
        Status = DeliveryStatus.RequiresNonPublicAccess,
        Reason = refusalMessage,
    };
}

/// <summary>
/// Options for <see cref="GuestEntryDeliveryResolver"/>. The Firebase web API key
/// (a public, client-shipped identifier — never a secret stored in this repo)
/// comes either from the CLI or from the user's own local client artifacts.
/// </summary>
public sealed record GuestEntryOptions
{
    /// <summary>Explicit web API key from the CLI. Optional; auto-detection runs when omitted.</summary>
    public string? FirebaseApiKey { get; init; }

    /// <summary>Local unpacked cluster client package directory (auto-detect source 2).</summary>
    public string? ClientPackageDirectory { get; init; }

    /// <summary>Local IL2CPP dump directory containing stringliteral.json (auto-detect source 1).</summary>
    public string? Il2CppDumpDirectory { get; init; }

    /// <summary>Public API options (base URL/headers). Tests point BaseUrl at a stub server.</summary>
    public ClusterApiOptions Api { get; init; } = new();

    /// <summary>
    /// The single string the enter_v2 payload carries. The normal client sends
    /// device/analytics metrics; this tool sends an honest, minimal, self-identifying
    /// payload instead of fabricated client metrics.
    /// </summary>
    public string MetricsPayloadJson { get; init; } =
        """{"entryMode":"cluster-map-downloader-guest-download"}""";

    /// <summary>
    /// Platform/device header values for the guest-entry round-trip. Cluster's
    /// room routes gate on platform (406 platform_not_supported otherwise);
    /// the anonymous-guest chain was live-verified with the desktop-style
    /// 'Win' values, and 'Web' is not an in-room client platform.
    /// </summary>
    public string Device { get; init; } = "Win";

    public string Platform { get; init; } = "Win";
}

/// <summary>
/// Explicit, documented opt-in delivery resolver (--guest-entry).
///
/// It reproduces the normal public client guest flow for exactly one specified
/// public world — nothing more, nothing sneakier (chain verified live,
/// October 2026):
///   1. Firebase anonymous sign-up (the same signupNewUser REST call every fresh
///      client install performs), using a web API key located from the user's
///      own CLI argument or local client artifacts (never hard-coded),
///   2. POST /v1/login with the anonymous Bearer idToken and an empty JSON body —
///      cluster links the Firebase identity to a fresh anonymous account
///      (UserPrivateResponse, isAnonymous=true). Without this step every room
///      route answers 401 BAD_TOKEN_EXIST,
///   3. POST /v1/world_room_set/&lt;worldRoomSetId&gt;/entry_request → runtime roomId
///      (requests carry X-Cluster-LoginID, and X-Cluster-Device/-Platform "Win" —
///      room routes gate on platform: 'Web' is not an in-room platform),
///   4. POST /v1/rooms/&lt;roomId&gt;/enter_v2 → RoomEnterResponse,
///   5. maps RoomVenue.platformResources (assets / mainScene / subScenes) into
///      the normal download pipeline.
///
/// Policy notes:
/// - Default behavior (no --guest-entry flag) is unchanged and stays at the
///   no-authentication boundary; this mode never runs silently.
/// - Tokens live in memory for the duration of one run; they are never written
///   to disk, never logged, and never embedded in manifests.
/// - Some venue bundles are AES-encrypted by the game client (AesAssetDecryptor).
///   This tool downloads those bytes as-is and does not attempt any
///   decryption/key extraction (that stays inside the game client).
/// - Single world, single run; no enumeration, no bulk scraping, no bypass of
///   any access control. Users are responsible for complying with cluster's
///   terms and for respecting creators' rights.
/// </summary>
public sealed class GuestEntryDeliveryResolver : IWorldDeliveryResolver
{
    private readonly GuestEntryOptions _options;
    private readonly HttpMessageHandler? _handler;

    public GuestEntryDeliveryResolver(GuestEntryOptions options, HttpMessageHandler? handler = null)
    {
        _options = options;
        _handler = handler;
    }

    public async ValueTask<DeliveryResolution> ResolveAsync(DeliveryRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await ResolveCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (GuestEntryRefusalException refusal)
        {
            return refusal.Resolution;
        }
    }

    private async Task<DeliveryResolution> ResolveCoreAsync(DeliveryRequest request,
        CancellationToken cancellationToken)
    {
        var worldId = request.Metadata.Id;
        if (string.IsNullOrWhiteSpace(worldId))
        {
            throw new InvalidOperationException(
                "World metadata did not contain the world id required for the guest entry flow.");
        }

        var locatedKey = FirebaseApiKeyLocator.Locate(
            _options.FirebaseApiKey,
            _options.ClientPackageDirectory,
            _options.Il2CppDumpDirectory);

        using var auth = new FirebaseGuestAuthenticator(_handler);
        var credential = await auth.SignInAnonymouslyAsync(locatedKey.Value, cancellationToken).ConfigureAwait(false);

        // Room routes gate on platform (406 platform_not_supported for 'Web');
        // the anonymous-guest chain is live-verified with desktop-style 'Win'
        // headers, so the guest API client overrides the metadata defaults.
        var guestApiOptions = _options.Api with
        {
            Device = _options.Device,
            Platform = _options.Platform,
        };
        using var api = new RoomEntryApiClient(guestApiOptions, _handler);

        GuestLoginResponse login;
        try
        {
            login = await api.LoginAsync(credential.IdToken, cancellationToken).ConfigureAwait(false);
        }
        catch (ClusterApiException ex) when (IsRefusal(ex))
        {
            throw CreateRefusal(ex, "guest login");
        }

        var loginUserId = login.UserId;
        if (string.IsNullOrWhiteSpace(loginUserId))
        {
            throw new ClusterApiException(
                "The cluster guest login succeeded but did not return a userId; guest entry cannot proceed.");
        }

        api.SetLoginId(loginUserId);

        CreateEntryRequestResponse entryRequest;
        try
        {
            entryRequest = await api
                .CreateWorldRoomSetEntryRequestAsync(worldId, credential.IdToken, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ClusterApiException ex) when (IsRefusal(ex))
        {
            throw CreateRefusal(ex, "world entry request");
        }

        var roomId = entryRequest.RoomId;
        if (string.IsNullOrWhiteSpace(roomId))
        {
            throw new ClusterApiException(
                "The world entry request succeeded but did not return a roomId; guest entry cannot proceed.");
        }

        RoomEnterResponse enterResponse;
        try
        {
            enterResponse = await api
                .EnterRoomV2Async(roomId, _options.MetricsPayloadJson, credential.IdToken, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ClusterApiException ex) when (ex.StatusCode == 400)
        {
            // The payload shape is server-validated; retry once with an empty
            // metrics string before treating the flow as refused (2 attempts total).
            try
            {
                enterResponse = await api
                    .EnterRoomV2Async(roomId, string.Empty, credential.IdToken, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ClusterApiException retryEx) when (IsRefusal(retryEx) || retryEx.StatusCode == 400)
            {
                throw CreateRefusal(retryEx, "room enter (also tried an empty metrics payload)");
            }
        }
        catch (ClusterApiException ex) when (IsRefusal(ex))
        {
            throw CreateRefusal(ex, "room enter");
        }

        var platformResources = enterResponse.Venue?.PlatformResources;
        if (platformResources is null)
        {
            throw new ClusterApiException(
                "The room-enter response did not contain venue.platformResources; no assets can be described.");
        }

        var assets = MapAssets(platformResources);
        if (assets.Count == 0)
        {
            throw new ClusterApiException(
                "The venue platformResources contained no assetBundleUrl entries; nothing to download.");
        }

        var venueDescription = enterResponse.Venue is { } venue
            ? $"venue '{venue.VenueId ?? "-"}' (revision {venue.Revision?.ToString() ?? "-"})"
            : "unknown venue";

        return new DeliveryResolution
        {
            Status = DeliveryStatus.Available,
            Reason = "Resolved through the documented opt-in guest-entry mode, which reproduces the normal " +
                     $"public client guest flow for this single world ({venueDescription}): anonymous Firebase " +
                     "guest sign-up, cluster guest login, world entry request, room-enter v2, then the " +
                     "platformResources asset list the client itself downloads. Tokens stayed in memory for " +
                     "this run only. Some bundles may be AES-encrypted by the game client; those are saved as " +
                     "encrypted bytes with no decryption.",
            Assets = assets,
        };
    }

    private static IReadOnlyList<AssetDownload> MapAssets(PlatformResources resources)
    {
        var assets = new List<AssetDownload>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddEntry(string kind, int index, string? url)
        {
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return;
            }

            if (!seen.Add(uri.AbsoluteUri))
            {
                return;
            }

            var prefix = $"{kind}-{index:D2}";
            assets.Add(new AssetDownload(uri, $"bundles/{prefix}-{MakeFileName(uri)}"));
        }

        var assetList = resources.Assets ?? Array.Empty<UnityAssetEntry>();
        for (var i = 0; i < assetList.Count; i++)
        {
            AddEntry("asset", i, assetList[i].AssetBundleUrl);
        }

        AddEntry("mainScene", 0, resources.MainScene?.AssetBundleUrl);

        var subScenes = resources.SubScenes ?? Array.Empty<UnityResourceEntry>();
        for (var i = 0; i < subScenes.Count; i++)
        {
            AddEntry("subScene", i, subScenes[i].AssetBundleUrl);
        }

        return assets;
    }

    private static string MakeFileName(Uri uri)
    {
        var lastSegment = uri.Segments.Length > 0 ? uri.Segments[^1] : string.Empty;
        var cleaned = Uri.UnescapeDataString(lastSegment).TrimEnd('/');
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = "bundle.bin";
        }

        var safe = Regex.Replace(cleaned, "[^A-Za-z0-9._-]", "_");
        return safe.Length > 96 ? safe[..96] : safe;
    }

    /// <summary>Guest-flow refusals map to the honest "requires non-public access" outcome (exit code 5).</summary>
    private static GuestEntryRefusalException CreateRefusal(ClusterApiException exception, string stage)
    {
        var maskedDetail = SecretMasker.Mask(exception.Message);
        var message =
            $"The normal guest entry flow ({stage}) was refused by cluster ({maskedDetail}). This tool uses " +
            "only the anonymous guest path the free client itself uses and never fabricates credentials, so " +
            "the run stops here. Anything beyond guest access (accounts, tickets, invitations) is out of scope " +
            "for this project by policy.";
        return new GuestEntryRefusalException(message, exception);
    }

    private static bool IsRefusal(ClusterApiException exception)
        => exception.StatusCode is 400 or 401 or 403 or 404 or 409 or 410;
}
