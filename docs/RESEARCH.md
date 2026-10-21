# Research: how cluster delivers world data, and where the no-auth boundary sits

> **Status: complete (October 2026).** The delivery mechanism has been
> identified by research. This document records every step so the conclusion
> is reproducible and auditable.
>
> Two delivery modes exist now:
> * **default (no flags):** the tool stops at this project's no-authentication
>   boundary — venue AssetBundles are only obtainable after the client's
>   guest-authenticated room-enter flow — with exit code `2`.
> * **explicit opt-in (`--guest-entry`):** documented in §8, the tool
>   reproduces the *normal public client guest flow* (anonymous Firebase
>   sign-in → entry request → room-enter v2 → the platformResources asset
>   list) for exactly one specified public world. No credentials, no
>   decryption, no enumeration. It runs only when asked for.

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
    "assets": [
      { "id": "...", "assetBundleUrl": "https://.../<bundle>.bundle" }
    ],
    "mainScene": {
      "assetBundleUrl": "https://.../<scene>.bundle",
      "assetIdsDependsOn": ["..."],
      "unitySceneName": "..."
    },
    "subScenes": [
      {
        "assetBundleUrl": "https://.../<scene>.bundle",
        "assetIdsDependsOn": ["..."],
        "unitySceneName": "..."
      }
    ]
  }
}
```

Field names verified against the IL2CPP type dump
(`ClusterVR.Infrastructure.Api.RequestAndResponse.Generated`:
`PlatformResources` → `assets: List<UnityAsset>`, `mainScene: UnityResource`,
`subScenes: List<UnityResource>`; `UnityAsset` = `{id, assetBundleUrl}`;
`UnityResource` = `{assetBundleUrl, assetIdsDependsOn[], unitySceneName}`.
Field *values* above are placeholders. `RoomEnterResponse` itself also carries
`roomId`, `entryId`, `worldRoomSetId`, `worldType` and many other members.

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

#### 3.4.1 How the client authenticates (IL2CPP evidence)

The client's auth chain, for a *fresh install / guest session*:

1. **Anonymous Firebase sign-in.**
   `ClusterVR.Infrastructure.Firebase.Authenticator.FirebaseAuthenticator` holds
   the URL constants `GoogleSignUpUrl =
   https://www.googleapis.com/identitytoolkit/v3/relyingparty/signupNewUser?key={0}`
   (legacy identitytoolkit v3 endpoint) plus the modern
   `GoogleCustomTokenSignInUrl =
   https://identitytoolkit.googleapis.com/v1/accounts:signInWithCustomToken?key={0}`
   and `GoogleIdentityUrl = …verifyAssertion?key={0}`. Anonymous sign-up sends
   `{"returnSecureToken":true}` (`AnonymousSignInParams`); the response is a
   `FirebaseCredential { idToken, refreshToken, … }` (`FirebaseRefreshResponse`
   fields: `access_token, expires_in, id_token, project_id, refresh_token,
   token_type, user_id`; refresh posts `FirebaseRefreshRequestParams
   {code, grant_type, refresh_token}` to
   `https://securetoken.googleapis.com/v1/token?key=`).
2. **Every API request carries `Authorization: Bearer <idToken>`.**
   `RimuldarApiHeaderProcessor` (`ClusterVR.Infrastructure.Api.Client`) has
   `OAuth2AuthorizationKey = "Authorization"`, `Bearer = "Bearer"`,
   `AddOAuth2AuthorizationHeaderValues(www, idToken)` and
   `BuildBearerToken(token)`; it also adds the `X-Cluster-*` headers
   (`X-Cluster-App-Version`, `X-Cluster-Build-Version`, `X-Cluster-Device`,
   `X-Cluster-Platform`, `X-Cluster-LoginID`, `X-Cluster-Analytics`,
   `Accept-Language`), exactly the header set the public web client uses.
3. **World entry needs a runtime room.** Rooms belong to a world room set
   (`Room.worldRoomSetId`, route literals `/v1/world_room_set/` and
   `/entry_request`). `CreateEntryRequestService.CreateEntryRequestAsync(
   WorldRoomSetId wrsId, …)` → `POST /v1/world_room_set/{wrsId}/entry_request`
   returns `CreateEntryRequestResponse { deepLink, roomId }`; that runtime
   `roomId` is then passed to `EnterToRoomV2Async(roomId, queryString, …)`.
   (`POST /v1/rooms/{roomId}/enter_v2` body: `PostEnterV2RoomPayload
   { metricsPayload: string }`.)
4. **Bundle URLs are absolute and fetched without API auth** (plain
   UnityWebRequest; see §3.3). `GET /v1/account/firebase_auth_custom_token`
   (`FirebaseAuthCustomToken { token }` → Firebase custom token) exists as an
   account→Firebase bridge; the anonymous sign-up path above is what a guest
   session uses on a fresh install (`FirebaseTokenRefresher` runs at app
   start).

### 3.5 Conclusion

World **metadata** is public; world **content** (`platformResources`)
arrives only as part of the guest-authenticated room-enter flow. The default
tool behavior therefore stops at the delivery stage with exit code `2` and
prints `World asset download is not implemented yet.`

There is, however, an honest way to describe that flow without any bypass:
it is the same anonymous guest flow a fresh, free client install performs
end to end. §8 documents the explicit, opt-in `--guest-entry` mode that
implements exactly that equivalence — it is never enabled by default, never
silent, and bounded to one specific world per run.

## 4. Why the default (no flags) stops where it stops

1. **No public endpoint exists** for the manifest — every probe and every
   code path goes through the guest-authenticated enter flow.
2. Running a guest-authenticated flow **by default** would silently turn the
   tool into an automated client; this project treats silent automation as a
   policy decision the user must make explicitly (the flag does exactly that).
3. Any *other* trick (stolen credentials, forged tokens, key/DRM extraction)
   would be an access-control bypass by definition and is out of scope
   forever, not merely "not yet done".

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

## 8. Guest-entry mode (documented, explicit opt-in)

**Status: implemented as `--guest-entry`.** Default behavior without the flag
is unchanged (exit 2 at the delivery boundary).

### 8.1 What it does, end to end

Given one public world, the resolver
([`GuestEntryDeliveryResolver.cs`](../src/ClusterMapDownloader/GuestEntry/GuestEntryDeliveryResolver.cs))
performs the exact request chain a fresh public client performs:

1. **Locate the Firebase web API key at runtime** (never stored in this repo):
   CLI/GUI `--firebase-api-key`, or scan of the user's local artifacts
   (`--il2cpp-dump <dir>/stringliteral.json`, then the Unity serialized
   data `*_Data/resources.assets` — the observed October 2026 carrier — plus
   `*_Data/**/global-metadata.dat`, looking for the AIza-shaped value).
   The package directory can be passed explicitly (`--client-package <dir>`)
   or is auto-discovered from the user's own machine (`ClientPackageLocator`:
   the working-tree `.work/client-package` convention, the registry uninstall
   entry the cluster installer wrote (HKEY_CURRENT_USER / HKEY_LOCAL_MACHINE
   `...\Uninstall`, including `WOW6432Node`, matching "cluster" in the key or
   display name with an `InstallLocation`), and well-known
   `%LOCALAPPDATA%\Programs\cluster*` / Program Files directories; every
   candidate is verified to actually contain those Unity data files before
   use). Ambiguous (multiple distinct values) ⇒ hard error with
   instructions. The key is a public, client-shipped project identifier,
   not a secret — but the repo still refuses to vendor it.
2. **Anonymous Firebase sign-in** (same REST call as every fresh client
   install): `POST
   https://www.googleapis.com/identitytoolkit/v3/relyingparty/signupNewUser?key=<webApiKey>`
   with body `{"returnSecureToken":true}` → `idToken/refreshToken`
   (in memory only, masks on every error path).
3. **Cluster guest login** (live-verified October 2026; the step a fresh
   client performs right after sign-up,
   `ApiUserRepository.LoginAsync` → `RimuldarApiClient.LoginAsync`):
   `POST https://api.cluster.mu/v1/login` with
   `Authorization: Bearer <idToken>` and an empty JSON body `{}` →
   HTTP 201 `UserPrivateResponse` (`{userId, isAnonymous:true, …}`) —
   cluster links the Firebase identity to a fresh anonymous account.
   Without this step every room route answers `401 BAD_TOKEN_EXIST`.
   The response's `userId` travels as the `X-Cluster-LoginID` header on
   the room routes afterwards (missing ⇒ `400 BAD_REQUEST_ROOM_ENTER
   "room entry request not exist"`).
4. **World entry request**: `POST
   https://api.cluster.mu/v1/world_room_set/{worldId}/entry_request`
   with `Authorization: Bearer <idToken>`, `X-Cluster-LoginID` and
   desktop-style `X-Cluster-Device: Win` / `X-Cluster-Platform: Win`
   headers → HTTP 201 `{"roomId": "…", "deepLink": "…",
   "presentedFreeCCAmount": 0}`. Room routes gate on platform
   (`406 platform_not_supported` outside
   `Android/Oculus/NowGG/iOS/Mac/Win`; `'Web'` is not an in-room
   platform, so the guest client overrides the metadata-stage headers).
   The `roomId` is the runtime room of the world's world-server (a GUID
   reused across guests within the same room set), not a `room_`-prefixed id.
5. **Room enter v2**: `POST
   https://api.cluster.mu/v1/rooms/{roomId}/enter_v2` with the client's
   payload shape `{"metricsPayload":"…"}` (this tool sends an honest,
   self-identifying string, not fabricated device metrics; one retry with
   an empty string on `400`) → HTTP 201 `RoomEnterResponse`, whose
   `venue.platformResources` carries
   `assets[]` / `mainScene` / `subScenes[]` with absolute `assetBundleUrl`s.
6. **Download**: the plain HTTP file downloader fetches each bundle into
   `output/<worldId>/bundles/…` and writes `downloads-manifest.json`
   (same pipeline as the public-metadata stages; URLs are absolute and
   unauthenticated exactly as the client fetches them). The manifest records
   per file a `fileFormat` column — `unityfs-plain` when the leading bytes
   are `55 6E 69 74 79 46 53 00` (`"UnityFS\0"`, a plain Unity asset bundle)
   or `unknown-probably-encrypted` otherwise (possibly
   `AesAssetDecryptor`-encrypted; saved as-is, never transformed) — and a
   lowercase-hex `sha256` integrity column.

Live end-to-end verification (October 2026): with the key located from a
local `*_Data/resources.assets`, the whole chain above succeeded against
the production API for the public sample world
`https://cluster.mu/w/c59fd20a-85b9-45ad-a357-4fdece9c180b`
(`venueSize` 118,100,853 bytes) — one `mainScene` bundle
(`assets=[]`, `subScenes=[]` for this venue), downloaded 118,100,853
bytes, file header `UnityFS` (a plain Unity asset bundle; venues whose
bundles are `AesAssetDecryptor`-encrypted stay encrypted and are saved
as-is). The same flow also runs unattended through the Windows GUI
(`cluster-map-downloader-gui.exe <url> -o <dir> --auto-close`), which
shares `GuestEntryResolverFactory` with the CLI and reports per-file,
byte-level progress; its manifest labeled the bundle
`fileFormat: unityfs-plain`.

### 8.2 The guarantees this mode keeps

| Rule | Implementation |
|---|---|
| Default stays at the no-auth boundary | Resolver is constructed only from `--guest-entry`; `NotImplementedDeliveryResolver` remains the default |
| No credentials | Anonymous Firebase sign-up only; no account login, no password/OAuth token flow, no custom-token bridge |
| No secret literals in the repo | Key found at runtime or passed per run; `SecretMasker` scrubs AIza/JWT shapes from every message |
| Tokens never persist | Firebase credential lives in the run's memory; not written to disk, not in manifests |
| Single world, no enumeration | One URL → one run → one login + one entry_request + one enter_v2; no listing/scraping loops |
| No decryption / DRM work | `AesAssetDecryptor`-protected bundles are saved as-is; key extraction stays out of scope forever |
| Honest payload | `metricsPayload` self-identifies the tool instead of fabricating client analytics |
| Refusals stay honest | 4xx from cluster maps to `DeliveryStatus.RequiresNonPublicAccess` (exit 5) with the server's masked reason |

### 8.3 Responsibility of the user

This mode automates requests the free client performs for every visitor of a
public world, and it does nothing the client does not do (no crafted
requests, no endpoint guessing, no credential use). Nevertheless: users are
responsible for complying with cluster's Terms of Service at the time they
run the tool, for respecting world creators' rights over their content
(see [NOTICE.md](../NOTICE.md)), and for keeping downloads personal.
