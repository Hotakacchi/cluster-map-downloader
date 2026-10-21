# Public cluster_web API — what this tool uses

> Scope: only the **public, unauthenticated** surface needed for world
> metadata. Probing results for neighbouring routes are documented for
> completeness so nobody has to rediscover the same walls. Everything here
> was verified live in **October 2026** from a normal browser/client
> context, nothing is guessed or brute-forced.

## Base URL & headers

| Item | Value |
|---|---|
| Base | `https://api.cluster.mu/v1` |
| User-Agent | `cluster-map-downloader/{toolVersion}` (plain product token; no parentheses/comments) |
| `X-Cluster-App-Version` | observed web client value, at research time `3.103.2610071832` (overridable via `--app-version`) |
| `X-Cluster-Build-Version` | observed web client build id, at research time `2610141027` (overridable via `--build-version`) |
| `X-Cluster-Device` / `X-Cluster-Platform` | `Web` / `Web` |
| `Accept` | `application/json` |
| `Authorization` | never sent — this tool implements no auth flow |

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
session; see [RESEARCH.md](RESEARCH.md) §3 for the full chain and the
project's boundary decision at that point.

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
