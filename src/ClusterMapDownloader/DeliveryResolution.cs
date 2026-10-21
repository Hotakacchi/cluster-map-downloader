namespace ClusterMapDownloader;

/// <summary>Outcome of resolving the client-side world delivery mechanism.</summary>
public enum DeliveryStatus
{
    /// <summary>A normal public delivery path exists and asset downloads are described.</summary>
    Available,

    /// <summary>No public delivery path is identified yet (current default).</summary>
    NotImplemented,

    /// <summary>Downloading would require authorization the normal public flow does not provide.</summary>
    RequiresNonPublicAccess,
}

/// <summary>A single asset the downloader should fetch.</summary>
/// <param name="Url">Absolute URL of the asset.</param>
/// <param name="RelativeFileName">File name (with optional relative directories) under the output directory.</param>
/// <param name="ExpectedSizeBytes">Optional expected size used for resume/skip checks.</param>
public sealed record AssetDownload(Uri Url, string RelativeFileName, long? ExpectedSizeBytes = null);

/// <summary>Request handed to <see cref="IWorldDeliveryResolver"/>.</summary>
public sealed record DeliveryRequest(WorldMetadata Metadata, VenueResolution Venue);

/// <summary>Resolution result. When <see cref="Status"/> is
/// <see cref="DeliveryStatus.Available"/>, <see cref="Assets"/> must describe what to download.</summary>
public sealed record DeliveryResolution
{
    public required DeliveryStatus Status { get; init; }

    /// <summary>Human-readable explanation (shown when download is not possible).</summary>
    public required string Reason { get; init; }

    /// <summary>Concrete next research steps, shown to guide the investigation.</summary>
    public IReadOnlyList<string> NextResearchSteps { get; init; } = Array.Empty<string>();

    /// <summary>Assets to download (only when <see cref="Status"/> is <see cref="DeliveryStatus.Available"/>).</summary>
    public IReadOnlyList<AssetDownload> Assets { get; init; } = Array.Empty<AssetDownload>();
}

/// <summary>
/// Extension point: implement this and hand it to
/// <see cref="WorldDownloadOptions.DeliveryResolver"/> once the client-side
/// delivery mechanism for a venue is understood.
/// </summary>
public interface IWorldDeliveryResolver
{
    /// <summary>Resolves the delivery mechanism for the given world/venue.</summary>
    ValueTask<DeliveryResolution> ResolveAsync(DeliveryRequest request, CancellationToken cancellationToken = default);
}
