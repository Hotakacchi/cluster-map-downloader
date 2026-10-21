using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClusterMapDownloader.GuestEntry;

/// <summary>
/// Room-enter response models. Field names come from the IL2CPP dump
/// (ClusterVR.Infrastructure.Api.RequestAndResponse.Generated: RoomEnterResponse,
/// RoomVenue, PlatformResources, UnityAsset, UnityResource). Unknown JSON fields
/// are preserved via extension data so nothing is silently dropped.
/// </summary>
public sealed record RoomEnterResponse
{
    [JsonPropertyName("roomId")]
    public string? RoomId { get; init; }

    [JsonPropertyName("entryId")]
    public string? EntryId { get; init; }

    [JsonPropertyName("worldRoomSetId")]
    public string? WorldRoomSetId { get; init; }

    [JsonPropertyName("worldType")]
    public string? WorldType { get; init; }

    [JsonPropertyName("venue")]
    public RoomVenue? Venue { get; init; }

    /// <summary>Any additional response fields (kept verbatim).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

public sealed record RoomVenue
{
    [JsonPropertyName("venueId")]
    public string? VenueId { get; init; }

    [JsonPropertyName("venueKey")]
    public string? VenueKey { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("ownerUserId")]
    public string? OwnerUserId { get; init; }

    [JsonPropertyName("revision")]
    public int? Revision { get; init; }

    [JsonPropertyName("revisionCreatedAt")]
    public string? RevisionCreatedAt { get; init; }

    [JsonPropertyName("unityVersion")]
    public string? UnityVersion { get; init; }

    [JsonPropertyName("renderPipelinePresetId")]
    public string? RenderPipelinePresetId { get; init; }

    [JsonPropertyName("platformResources")]
    public PlatformResources? PlatformResources { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>
/// The venue asset manifest:
/// <code>
/// { "assets": [ {"id": utc…, "assetBundleUrl": "https://…"} ],
///   "mainScene": { "assetBundleUrl": "https://…", "assetIdsDependsOn": ["…"], "unitySceneName": "…" },
///   "subScenes": [ … ] }
/// </code>
/// </summary>
public sealed record PlatformResources
{
    [JsonPropertyName("assets")]
    public IReadOnlyList<UnityAssetEntry>? Assets { get; init; }

    [JsonPropertyName("mainScene")]
    public UnityResourceEntry? MainScene { get; init; }

    [JsonPropertyName("subScenes")]
    public IReadOnlyList<UnityResourceEntry>? SubScenes { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

public sealed record UnityAssetEntry
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("assetBundleUrl")]
    public string? AssetBundleUrl { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

public sealed record UnityResourceEntry
{
    [JsonPropertyName("assetBundleUrl")]
    public string? AssetBundleUrl { get; init; }

    [JsonPropertyName("assetIdsDependsOn")]
    public IReadOnlyList<string>? AssetIdsDependsOn { get; init; }

    [JsonPropertyName("unitySceneName")]
    public string? UnitySceneName { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>Response of the world-room-set entry request: the runtime room id to enter.</summary>
public sealed record CreateEntryRequestResponse
{
    [JsonPropertyName("roomId")]
    public string? RoomId { get; init; }

    [JsonPropertyName("deepLink")]
    public string? DeepLink { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>
/// Subset of the POST /v1/login response (UserPrivateResponse) the guest flow
/// needs. Cluster issues a fresh anonymous account per device/session
/// (isAnonymous=true); the userId travels as the X-Cluster-LoginID header on
/// room routes. Everything else stays available in <see cref="Extra"/>.
/// </summary>
public sealed record GuestLoginResponse
{
    [JsonPropertyName("userId")]
    public string? UserId { get; init; }

    [JsonPropertyName("isAnonymous")]
    public bool? IsAnonymous { get; init; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("shareUrl")]
    public string? ShareUrl { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
