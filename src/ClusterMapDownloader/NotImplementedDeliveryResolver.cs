namespace ClusterMapDownloader;

/// <summary>
/// The built-in resolver: reports that world-asset download is not implemented.
///
/// The delivery mechanism itself was identified through documented local research
/// (see docs/RESEARCH.md): venue AssetBundles are delivered only through the
/// room-enter flow, which requires a Firebase access token. Implementing that
/// flow would mean emulating an authenticated client session, so this tool stops
/// at the no-authentication boundary instead of proceeding. It never pretends
/// to download world assets.
/// </summary>
public sealed class NotImplementedDeliveryResolver : IWorldDeliveryResolver
{
    public static NotImplementedDeliveryResolver Instance { get; } = new();

    public ValueTask<DeliveryResolution> ResolveAsync(DeliveryRequest request,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new DeliveryResolution
        {
            Status = DeliveryStatus.NotImplemented,
            Reason =
                "Venue AssetBundles are not served by any public unauthenticated endpoint that this tool can " +
                "honestly use. The venue bundle manifest (RoomVenue.platformResources with assetBundleUrl " +
                "entries per UnityResource/UnityAsset) is delivered by the desktop client only through the " +
                "room-enter flow (POST /v1/rooms/{roomId}/enter_v2 or GET /v1/rooms/{roomId}/enter_info). " +
                "Local probing shows both require a Firebase access token (401 BAD_TOKEN_EXIST with only the " +
                "public X-Cluster-* headers used by the web client). Reproducing guest authentication is beyond " +
                "the public no-authentication boundary this project enforces, so the tool stops here.",
            NextResearchSteps = new[]
            {
                "Fact (researched & documented in docs/RESEARCH.md): venue bundles load from absolute URLs inside " +
                "RoomVenue.platformResources, via VenueAssetBundleLoaderV2.BundleLoader.LoadAsync (UnityWebRequest).",
                "Fact: obtaining RoomVenue requires cluster's normal guest/auth flow; the desktop client uses " +
                "Firebase auth (Authorization header) for /v1/rooms/*/enter_* routes.",
                "Decision recorded: this project does not emulate authenticated sessions, so no next step is " +
                "carried out; a future explicit, documented opt-in mode would be required. Contributions must " +
                "state an equivalence to the normal public client flow per docs/ARCHITECTURE.md.",
                "Caveat documented: some venue assets pass through AesAssetDecryptor (IAssetDecryptor) after " +
                "download, so raw bundle bytes may not be directly usable.",
            },
        });
}
