using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClusterMapDownloader;

/// <summary>Public creator block of the world metadata response.</summary>
public sealed record WorldCreator
{
    [JsonPropertyName("userId")]
    public string? UserId { get; init; }

    [JsonPropertyName("username")]
    public string? Username { get; init; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    /// <summary>Optional display-rank / certification fields are preserved via extension data.</summary>
}

/// <summary>
/// World metadata as returned by the public web API
/// <c>GET https://api.cluster.mu/v1/worlds/{worldId}</c>.
///
/// Field names use the JSON property names of the live API; unknown fields
/// are preserved in <see cref="Extra"/> so nothing is lost between versions.
/// </summary>
public sealed record WorldMetadata
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Internal venue identifier, e.g. <c>venue_&lt;hex&gt;</c>. Drives asset delivery.</summary>
    [JsonPropertyName("venueId")]
    public string? VenueId { get; init; }

    /// <summary>Total size of the world content in bytes, as reported by the metadata endpoint.</summary>
    [JsonPropertyName("venueSize")]
    public long? VenueSize { get; init; }

    [JsonPropertyName("venueCreatedAt")]
    public string? VenueCreatedAt { get; init; }

    [JsonPropertyName("venueUpdatedAt")]
    public string? VenueUpdatedAt { get; init; }

    [JsonPropertyName("venuePublishedAt")]
    public string? VenuePublishedAt { get; init; }

    [JsonPropertyName("worldType")]
    public string? WorldType { get; init; }

    /// <summary>Observed values include <c>Public</c>. This tool supports public worlds only.</summary>
    [JsonPropertyName("privacyType")]
    public string? PrivacyType { get; init; }

    [JsonPropertyName("sdkType")]
    public string? SdkType { get; init; }

    [JsonPropertyName("sdkVersion")]
    public string? SdkVersion { get; init; }

    [JsonPropertyName("category")]
    public string? Category { get; init; }

    [JsonPropertyName("canEntry")]
    public bool? CanEntry { get; init; }

    [JsonPropertyName("betaEnabled")]
    public bool? BetaEnabled { get; init; }

    [JsonPropertyName("playCount")]
    public long? PlayCount { get; init; }

    [JsonPropertyName("likeCount")]
    public long? LikeCount { get; init; }

    [JsonPropertyName("webUrl")]
    public string? WebUrl { get; init; }

    [JsonPropertyName("thumbnailUrl")]
    public string? ThumbnailUrl { get; init; }

    [JsonPropertyName("originalImageUrl")]
    public string? OriginalImageUrl { get; init; }

    [JsonPropertyName("creator")]
    public WorldCreator? Creator { get; init; }

    /// <summary>Any additional response fields (kept verbatim).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>Shared System.Text.Json options used by this project.</summary>
public static class WorldMetadataJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
