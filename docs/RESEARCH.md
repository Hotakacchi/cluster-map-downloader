# Research: how cluster delivers world data, and where the no-auth boundary sits

> **Status: complete (October 2026).** The delivery mechanism has been
> identified by research, and it deliberately **stops at this project's
> no-authentication boundary**: venue AssetBundles are only obtainable after
> the client's authenticated room-enter flow. This document records every
> step so the conclusion is reproducible and auditable.

This document describes *research only*. The repository ships no client
binaries, no world assets, no disassembly dumps, and no secret/key literals —
see [NOTICE.md](../NOTICE.md) and [SECURITY.md](../SECURITY.md).

---

## 0. Ground rules

* Ask the **public/normal** flow only: documented endpoints, publicly served
  files, plain HTTP.
* No authentication bypass, no access-control bypass, no credential
  extraction, no token theft, no "brute force URL space" enumeration.
* A handful of requests per probe, sane `User-Agent`, respectful volume.
* Client/world content stays on the local machine and is never committed.

## 1. TL;DR

```text
worldId ──► GET/POST https://api.cluster.mu/v1/worlds/{worldId}  (public, OK)
worldId ──► metadata.venueId  (e.g. venue_7b06e33a0b3cec1e5fcaf5b028)
venue/roomId ─► POST /v1/rooms/{roomId}/enter_v2 or GET /v1/rooms/{roomId}/enter_info
                (needs a Firebase Authorization token; unauthenticated probes
                 return 401 BAD_TOKEN_EXIST)
        └► RoomEnterResponse.venue (RoomVenue JSON)
            └─ platformResources[]  (UnityAsset / UnityResource)
               each carries an absolute **assetBundleUrl**
                  └─ VenueAssetBundleLoaderV2.BundleLoader<T>.LoadAsync(...)  (UnityWebRequest)
                        └─ download (+ optional AesAssetDecryptor)
```

The world metadata and the room-enter flow are **different trust levels**:
world metadata is public, the room-enter response is only issued to an
authenticated (newly-joining client) session. There is **no public,
unauthenticated endpoint that serves `RoomVenue.platformResources`**.
Therefore a no-auth tool cannot fetch a world's AssetBundles and honestly
complete the pipeline — the tool stops at the delivery stage with
exit code `2` and prints `World asset download is not implemented yet.`

## 2. Method

| Phase | What was done | Tools / evidence kept locally |
|---|---|---|
| A. Public web client | Read the public cluster.mu web JavaScript bundle, searched for delivery-related strings (`AssetBundle`, `bundleUrl`, `downloadUrl`, `venueId`, `revision`, storage hosts). Found none: the website embeds no world-delivery endpoint. | browser dev-tools transcript (not committed) |
| B. Installer | `cluster_win.exe` is an **Inno Setup 6.7.0** bootstrap. `embedded\CompiledCode.bin` holds PascalScript bytecode; disassembled with [IFPSTools.NET](https://github.com/Wack0/IFPSTools.NET) via this repo's `tools/ifpsdasm-wrapper`. | `.temp/cluster-unpacked/` (ignored) |
| C. Client package | The bytecode downloads `https://update.cluster.mu/cluster_win/2/<per-release-suffix>/cluster_win.7z` **anonymously** (no auth headers). Extracted with 7-Zip. | `downloads/`, `.work/client-package/` (ignored) |
| D. Client anatomy | Unity 6 IL2CPP build: `cluster.exe`, `GameAssembly.dll` (~177 MiB), `UnityPlayer.dll`, `cluster_Data/` (`global-metadata.dat` ~33.6 MiB, `resources.assets`, levels), local-only `StreamingAssets/aa` Addressables catalog (`roomassetbuiltin` / `roomasseturp` bundles are baked in, not downloaded). | same |
| E. IL2CPP dump | `Il2CppDumper` v6.7.46 (net6) over `GameAssembly.dll` + `global-metadata.dat` → `dump.cs` (~110 MiB), `stringliteral.json`, `il2cpp.h`, `script.json`. All class/method facts below come from this dump. | `.work/il2cpp-dump/` (ignored) |
| F. Live probes | Small number of HTTP probes against documented routes with the web client's public headers, no tokens. | transcript below |

Hosts found inside the client (`resources.assets` / BuildSettings):
`https://api.cluster.mu`, `https://update.cluster.mu`, `https://cluster.mu`,
plus the `cl4r.net` telemetry host. Client build observed at research time:
`3.104.2610141842`, tag `rc/client/v3.104` (the web client pinned a slightly
older `3.103.2610071832` / build `2610141027`).

> Secret-value policy: the client binary embeds a public Firebase web API
> key, a Stripe publishable key and Google OAuth client IDs. These are
> **value classes**, not secrets to be copied; this project documents and
> masks their shape only ([SECURITY.md](../SECURITY.md)).

## 3. The delivery chain in detail (from the IL2CPP dump)

### 3.1 API client surface

* `RimuldarApiClient(Base)` (namespace `ClusterVR.Infrastructure.Api.Client`)
  wraps the **same** `X-Cluster-*` header set the web client uses, plus
  `Authorization: Bearer <Firebase token>` when logged in.
* World metadata: `GET /v1/worlds/{worldId}` — public.
* Room entry:
  * `POST /v1/rooms/{roomId}/enter_v2` with a `PostEnterV2RoomPayload`
    (contains an analytics/metrics payload);
  * `GET /v1/rooms/{roomId}/enter_info` (deep-link fetch used before
    entering);
  * response type `RoomEnterResponse` with an embedded `venue` member.

### 3.2 `RoomVenue` — the bundle manifest

`RoomEnterResponse.venue` carries a JSON object shaped like:

```json
{
  "venueId": "venue_<24+ hex digits>",
  "venueKey": "...",
  "name": "...",
  "ownerUserId": "...",
  "revision": 123,
  "revisionCreatedAt": "2026-05-14T07:53:28Z",
  "unityVersion": "6000.x.y",
  "renderPipelinePresetId": "...",
  "platformResources": {
    "assetResources": [
      { "id": "...", "assetBundleUrl": "https://.../<bundle>.bundle" }
    ],
    "sceneResources": [
      {
        "assetBundleUrl": "https://.../<scene>.bundle",
        "assetIdsDependsOn": ["..."],
        "unitySceneName": "..."
      }
    ]
  }
}
```

(Shape as observed in the IL2CPP type dumps; `platformResources` entries are
`UnityAsset`/`UnityResource` records. Field *values* above are placeholders.)

### 3.3 How the client turns that into files

`VenueAssetBundleLoaderV2.BundleLoader<T>.LoadAsync(resource, assetBundleUrl,
progress)` issues a UnityWebRequest against the **absolute** `assetBundleUrl`
from the manifest. After download, some assets pass through
`AesAssetDecryptor` (an `IAssetDecryptor` implementation) — a documented
caveat: raw bundle bytes from encrypted entries are not directly loadable.

### 3.4 The authentication wall (verified live)

| Probe | Result | Meaning |
|---|---|---|
| `GET /v1/rooms/{world GUID}/enter_v2` | `405 method_not_allowed` | route exists, POST-only |
| `GET /v1/rooms/{venue hex}/enter_v2` | `405` | same |
| `GET /v1/rooms/{roomId}/enter_v2` with `venue_…` form | `405` | same |
| `GET /v1/rooms/{roomId}/enter_info` (no token) | `401 BAD_TOKEN_EXIST` | requires Firebase `Authorization` |
| `GET /v1/rooms/{roomId}` (room GUID) | `404 UNREGISTERED_ROOM` | route exists unauthenticated but rooms are runtime sessions |
| `GET /v1/venues/venue_7b06e33a0b3cec1e5fcaf5b028` | `405` (PATCH-only) | venue is a management resource, not a read resource |
| `GET /v1/worlds/{world GUID}` | `200` | public metadata works without auth |

Error envelope observed on failures:

```json
{ "id": "...", "code": "BAD_TOKEN_EXIST", "status": 401, "detail": "...", "meta": {} }
```

### 3.5 Conclusion

World **metadata** is public; world **content** (`platformResources`)
arrives only as part of an authenticated room-enter flow. Reproducing that
flow means emulating a normal client session with Firebase guest/auth
credentials — which is exactly the boundary this project refuses to cross
without an explicitly documented, opt-in design discussion (the current
resolver simply reports `NotImplemented`; see
[`NotImplementedDeliveryResolver.cs`](../src/ClusterMapDownloader/NotImplementedDeliveryResolver.cs)).

## 4. Why this is the *right* stopping point for an open-source tool

1. **No public endpoint exists** for the manifest — every probe and every
   code path goes through the enter flow.
2. Any "anonymous" trick would be an access-control bypass by definition
   and is out of scope forever, not merely "not yet done".
3. Emulating authentication is possible for a *researcher*, but it changes
   the tool from "public-data helper" into "client impersonator"; that is a
   policy decision, not a research gap — and this project's published
   decision is **no** (see [ARCHITECTURE.md § Boundaries](ARCHITECTURE.md#boundaries)).

## 5. Verification matrix

| # | Claim | Evidence |
|---|---|---|
| 1 | `cluster_win.exe` = Inno Setup 6.7.0 bootstrap | innounp extraction logs (local); README |
| 2 | Installer fetches the 7z package anonymously | CompiledCode.dis.txt shows the `update.cluster.mu` URL and the fetch logic; download required no auth |
| 3 | Client is Unity 6 IL2CPP | package layout (`GameAssembly.dll`, `global-metadata.dat`), dump works |
| 4 | Room-enter routes exist | live probes (405/401 envelope) + IL2CPP surface |
| 5 | Manifest lives in `RoomVenue.platformResources` | IL2CPP classes + the loader call chain in `dump.cs` |
| 6 | Loader = UnityWebRequest per `assetBundleUrl` | `VenueAssetBundleLoaderV2.BundleLoader.LoadAsync` in `dump.cs` |
| 7 | Enter flow needs Firebase token (`401 BAD_TOKEN_EXIST`) | live probe without Authorization |
| 8 | Some bundles are AES-encrypted after download | `AesAssetDecryptor` (IL2CPP) |
| 9 | No public unauthenticated delivery endpoint | exhaustive probes documented above (no *455/402/200* route observed for stage content) |

## 6. Reproducing the research locally (opt-in, ignored dirs)

```powershell
# third-party tools (fetched at runtime; never vendored)
pwsh .\scripts\bootstrap.ps1 -WithTools -WithAnalysisTools

# installer → disassembly → package
pwsh .\scripts\extract-installer.ps1 -InstallerPath "C:\path\to\cluster_win.exe" -Disassemble `
      -DownloadClientPackage -ExtractClientPackage

# client package anatomy + IL2CPP dump
pwsh .\scripts\analyze-client.ps1 -Il2CppDump
```

These steps are **not** part of CI. Artifacts live in `.work/`, `.temp/`,
`downloads/` and are never committed.

## 7. If the conclusion changes

The tool contract (interface `IWorldDeliveryResolver`) is ready for a
resolver that can *honestly* complete the pipeline. Any such change must:

* document the equivalence with what a normal public client does (no
  special requests, no brute forcing);
* be reviewed against [SECURITY.md](../SECURITY.md) and the boundaries in
  [ARCHITECTURE.md](ARCHITECTURE.md);
* keep world/venue content handling local-only, with legal-use notes
  (copyrighted content — see [NOTICE.md](../NOTICE.md)).
