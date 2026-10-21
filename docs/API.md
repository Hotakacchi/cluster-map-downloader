# Public cluster_web API — what this tool uses

> Scope: the **public, unauthenticated** surface used for world metadata,
> plus the **normal guest-authenticated flow** used by the explicit opt-in
> `--guest-entry` mode (documented below and in
> [RESEARCH.md](RESEARCH.md) §8). Probing results for neighbouring routes
> are documented for completeness so nobody has to rediscover the same
> walls. Everything here was verified live in **October 2026** from a
> normal browser/client context, nothing is guessed or brute-forced.

## Base URL & headers

| Item | Value |
|---|---|
| Base | `https://api.cluster.mu/v1` |
| User-Agent | `cluster-map-downloader/{toolVersion}` (plain product token; no parentheses/comments) |
| `X-Cluster-App-Version` | observed web client value, at research time `3.103.2610071832` (overridable via `--app-version`) |
| `X-Cluster-Build-Version` | observed web client build id, at research time `2610141027` (overridable via `--build-version`) |
| `X-Cluster-Device` / `X-Cluster-Platform` | `Web` / `Web` for the unauthenticated metadata stages; with `--guest-entry` the room routes switch to `Win` / `Win` (room routes gate on platform, live-verified) |
| `X-Cluster-LoginID` | only with `--guest-entry`, after the `POST /v1/login` step: the anonymous account `userId` |
| `Accept` | `application/json` |
| `Authorization` | never sent by default; with `--guest-entry` only `Bearer <anonymous-guest idToken>` from the public identitytoolkit sign-up below |

These headers are plain version strings baked into the public web client
(they are not secrets and must not be confused with keys/tokens).

## Endpoints used by this tool

### `GET /v1/worlds/{worldId}` — public world metadata

* `{worldId}` is the (lower-case) GUID-like id from the world page URL.
* Success: HTTP 200 with a JSON object; see
  [`../samples/world-metadata.sample.json`](../samples/world-metadata.sample.json)
  for a shape sample (fully synthetic values).
* Failure: HTTP error object shaped `{"id","code","status","detail","meta"}`.

Field highlights the tool cares about (all strings nullable; unknown fields
keep verbatim in `Extra`):

| field | meaning |
|---|---|
| `id`, `name`, `description` | paging basics |
| `privacyType` | `Public` (this tool only supports that value) |
| `venueId` | internal venue id `venue_<hex>` — drives venue resolution |
| `venueSize` | total world content bytes (per API) |
| `venueCreatedAt` / `venueUpdatedAt` / `venuePublishedAt` | ISO-ish timestamps |
| `worldType`, `sdkType`, `sdkVersion`, `category` | descriptive |
| `webUrl`, `thumbnailUrl`, `originalImageUrl` | links |
| `creator` | `{userId, username, displayName, ...}` |

## Endpoints probed (documented for completeness, NOT used)

| Route | Probe result | Why not used |
|---|---|---|
| `GET /v1/venues/{world GUID}` | `405` | wrong id class; route is PATCH-oriented management space |
| `GET /v1/venues/venue_<hex>` | `405` | no public read endpoint for venue objects |
| `GET /v1/worlds/{venueId}` | `404` | id class mismatch |
| `GET /v1/rooms/{roomId}` | `404 UNREGISTERED_ROOM` with a room GUID | route exists unauthenticated but rooms are runtime sessions, not stable ids |
| `GET /v1/rooms/{roomId}/enter_v2` | `405 method_not_allowed` (POST-only) | part of the authenticated room-enter flow |
| `GET /v1/rooms/{roomId}/enter_info` | `401 BAD_TOKEN_EXIST` without a Firebase `Authorization` header | same |

Error envelope observed on those probes:
`{"id":"...","code":"BAD_TOKEN_EXIST","status":401,"detail":"...","meta":{}}`.

The room-enter responses (and thus `RoomVenue.platformResources` — the
venue bundle manifest) are only obtainable through the normal client
session; see [RESEARCH.md](RESEARCH.md) §3 for the full chain. The opt-in
`--guest-entry` mode reproduces that normal guest session explicitly
(documented below; default behavior without the flag stays unauthenticated
and stops at the boundary with exit `2`).

## Guest-entry mode routes (opt-in; verified from the IL2CPP dump + live)

The chain a fresh public client performs — and the only requests this mode
sends (one world per run, one attempt per stage, one payload retry at most):

| # | Request | Notes |
|---|---|---|
| 1 | `POST https://www.googleapis.com/identitytoolkit/v3/relyingparty/signupNewUser?key=<webApiKey>` body `{"returnSecureToken":true}` | exactly the client's `FirebaseAuthenticator.GoogleSignUpUrl` / `AnonymousSignInParams`; response `{idToken, refreshToken, expiresIn, localId}`; errors map to exit 5 |
| 2 | `POST /v1/login` with `Authorization: Bearer <idToken>`, body `{}` | live-verified (October 2026): cluster links the Firebase identity to a fresh anonymous account → HTTP 201 `UserPrivateResponse` `{userId, isAnonymous:true, …}`; without it every room route answers `401 BAD_TOKEN_EXIST`; the `userId` becomes the `X-Cluster-LoginID` header on room routes |
| 3 | `POST /v1/world_room_set/{worldId}/entry_request` with `Authorization: Bearer <idToken>` + `X-Cluster-LoginID` + `X-Cluster-Device/-Platform: Win` | client's `CreateWorldRoomSetEntryRequestAsync`; returns HTTP 201 `{"roomId": "...", "deepLink": "...", "presentedFreeCCAmount": 0}` — the runtime room (a reused world-server GUID). Room routes gate on platform: `406 platform_not_supported` unless the platform is one of `Android/Oculus/NowGG/iOS/Mac/Win` (`'Web'` is not an in-room platform) |
| 4 | `POST /v1/rooms/{roomId}/enter_v2` body `{"metricsPayload":"…"}` | client's `EnterToRoomV2Async`/`PostEnterV2RoomPayload`; this tool sends a self-identifying string (not device metrics) and retries once with an empty string on HTTP 400; response = HTTP 201 `RoomEnterResponse` with `venue.platformResources = {assets:[{id, assetBundleUrl}], mainScene:{assetBundleUrl, assetIdsDependsOn[], unitySceneName}, subScenes:[…]}` |
| 5 | `GET {assetBundleUrl}` | plain HTTP, no `Authorization` header (same as the client's `VenueAssetBundleLoaderV2`); files land under `output/<worldId>/bundles/` |

Policy notes: the Firebase web API key is a public client-shipped project
identifier — this repo never vendors it; it comes from the CLI or from the
user's local artifacts (`--il2cpp-dump` scans `stringliteral.json`;
`--client-package` scans `*_Data/resources.assets` — the key's observed
October 2026 carrier — plus `global-metadata.dat`).
Tokens stay in memory for the run only. Bundle URLs may deliver
`AesAssetDecryptor`-encrypted content; such files are saved as-is (the tool
never decrypts anything). Worlds requiring accounts/tickets/invitations are
refused by cluster and reported honestly (exit `5`).

## Etiquette / expectations

* A handful of requests per world; no endpoint rotation or enumeration.
* No retries beyond the HTTP client's default; `60 s` request timeout.
* Requests carry no cookies; the tool keeps no session state.
* Version strings are updated only from evidence, never arrived at by
  guessing.

## Version drift

All observed values were current October **2026**; cluster ships often, so
`--app-version` and `--build-version` exist to override per run. If the
metadata endpoint changes shape, unknown fields land in `Extra` and a
`venueId` mismatch surfaces as a clear warning/error (that is what
`VenueResolver` prints).
