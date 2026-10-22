# Architecture

This document maps the code, the pipeline, the extension points, the
PowerShell research support, and the boundaries that keep this project
redistributable and honest. Companion reading:
[RESEARCH.md](RESEARCH.md) and [API.md](API.md).

## 1. High-level pipeline

```text
                ┌──────────────────────────┐
 world URL ───► │  WorldUrl parser         │  (URL/ID validation, canonical form)
                └──────────┬───────────────┘
                           ▼
                ┌──────────────────────────┐
 worldId  ────► │  ClusterApiClient        │  GET https://api.cluster.mu/v1/worlds/{id}
                └──────────┬───────────────┘  (X-Cluster-* public headers, no auth)
                           ▼
                ┌──────────────────────────┐
 metadata ────► │  VenueResolver           │  venueId found? format looks valid?
                └──────────┬───────────────┘
                           ▼
                ┌──────────────────────────┐
 venue    ────► │  IWorldDeliveryResolver  │  resolve delivery mechanism
                └──────────┬───────────────┘   default: Not implemented → exit 2 & final message
                           ▼                     --guest-entry: normal client guest flow (see §3.2)
                ┌──────────────────────────┐
 assets   ────► │  FileDownloader          │  generic HTTP file pipeline
                └──────────┬───────────────┘
                           ▼
                  world-metadata.json / downloads-manifest.json / files
```

## 2. Projects

| Project | Purpose | Notes |
|---|---|---|
| `src/ClusterMapDownloader` | .NET 8 class library: URL parsing, API client, metadata models, venue resolution, guest-entry flow, download plumbing, orchestration | zero external network beyond the documented endpoints |
| `src/ClusterMapDownloader.Cli` | `cluster-map-downloader` CLI (`download`, `metadata`, `tidy-glb`, `version`, `help`) | exit-code contract documented below |
| `src/ClusterMapDownloader.Gui` | WinForms paste-a-URL GUI (`cluster-map-downloader-gui.exe`) - the same pipeline plus per-file/byte progress; always runs the guest-entry flow with compact, user-facing messages; a **Past downloads** picker lists previously downloaded worlds from the output directory (re-conversion without re-download), a **Converted models** picker + **Open 3D viewer** button replay past conversions in an in-app WebView2 + three.js window (WASD/Space fly camera), and a **Convert to GLB** button re-runs `scripts/convert-bundle.ps1` in the background (progress into the log, cancel kills the converter tree) | net8.0-windows; `EnableWindowsTargeting` keeps it buildable on non-Windows CI; `viewer/` page ships with the publish output |
| `tests/ClusterMapDownloader.Tests` | xUnit suite (80 tests), all synthetic (no copyrighted world data, no real keys) | see "Testing", §6 |
| `tools/ifpsdasm-wrapper` | net8.0 front-end for IFPSLib; turns the installer's `CompiledCode.bin` into readable disassembly | NOT part of the main solution — needs `.work/third-party/IFPSTools.NET` (bootstrap-fetched) |
| `scripts/convert-bundle.ps1` | Converts a downloaded `unityfs-plain` bundle into a single glTF 2.0 scene model (GLB: hierarchy + meshes + materials + embedded textures) for three.js/Unity/Blender | drives AssetRipper (GPL-3.0, pinned runtime fetch into `.work/third-party`, never vendored) headlessly over its local WebUI API; the scene GLB is named after the world's public map name (`world-metadata.json`); each copied scene GLB is then tidied in place by `GlbTidier` (missing vertex normals regenerated — three.js renders normal-less meshes black); the tidy outcome is recorded in the manifest's `tidy` section; magic-checks `UnityFS\0` first and skips encrypted files honestly (exit 2); all deliverables under git-ignored `output/<map name or worldId>/converted/` |
| `tools/threejs-viewer/index.html` | Single-file GLB viewer (three.js `GLTFLoader` + `OrbitControls` via CDN importmap; drag & drop or `?url=`) | pure tracked code; needs internet once for the CDN importmap; footer discloses personal-use-only |
| `scripts/` | PowerShell research helpers (see §4) | all outputs into git-ignored dirs |

## 3. Library surface (`ClusterMapDownloader`)

| Type | Role |
|---|---|
| `WorldUrl` | parse `https://cluster.mu/w/<id>`, mirrors, or bare id; canonical URL + GUID plausibility |
| `ClusterApiOptions` | base URL + header generation (per [API.md](API.md)); immutable record |
| `ClusterApiClient` / `ClusterApiException` | typed metadata client; 404 → friendly "world not found" |
| `WorldMetadata` / `WorldCreator` | response schema; unknown fields preserved in `Extra` |
| `VenueResolver` / `VenueResolution` | extracts `venueId`, checks `venue_<hex>` shape |
| `IWorldDeliveryResolver`, `DeliveryRequest/Resolution/Status` | **the boundary** — pluggable delivery decision point |
| `NotImplementedDeliveryResolver` | default resolver: reports `NotImplemented`, with reason + next research steps (see below) |
| `GuestEntry/*` | the opt-in guest-entry flow (see §3.2): `FirebaseApiKeyLocator`, `FirebaseGuestAuthenticator`, `RoomEntryApiClient`, room-entry models, `GuestEntryDeliveryResolver`, plus `ClientPackageLocator` (auto-discovery of a local client package) and `GuestEntryResolverFactory` (one shared construction point for CLI + GUI) |
| `SecretMasker` | scrubs AIza/JWT/refresh-token shapes from every message that could carry one |
| `AssetFileFormatInspector` | leading-byte classification of saved files (`unityfs-plain` / `unknown-probably-encrypted`) + SHA-256; honest labeling only — nothing is decrypted |
| `FileDownloader` | staged downloads, byte formatting, resume-friendly manifest rows, optional `DownloadedFileProgress` (per-file + byte-level) sink |
| `WorldDownloader` | the six-stage orchestrator (plus Stage 2.5: the world folder is named after the public map name when the API provides one, sanitized, with short-id/number suffixes on collisions; falls back to `<worldId>`); also writes `world-metadata.json` and `downloads-manifest.json` |
| `ExitCodes` | the CLI exit contract |
| `ToolInfo` | name/version/notice strings |
| `GlbTidier` / `GlbTidyResult` / `GlbTidyException` | in-place repair pass for exported GLB scene files: computes smooth per-vertex normals for triangle primitives that shipped without a NORMAL attribute (area-weighted accumulation from POSITION + indices; float32, 4-byte aligned, idempotent; unsupported shapes are honestly counted and skipped) — three.js GLTFLoader and other consumers render normal-less meshes unlit/black |

### 3.1 The delivery boundary (load-bearing)

`DeliveryStatus`:

| Status | CLI exit | Behavior |
|---|---|---|
| `Available` | 0 | proceeds to `FileDownloader` (set only by the opt-in guest-entry resolver) |
| `NotImplemented` | **2** | final message printed, no files, research keywords shown (default) |
| `RequiresNonPublicAccess` | 5 | same reporting, different classification |

The built-in resolver reports the state honestly with a reason and a list of
"next research steps"; one of them is a *hard no* — never bypass
authentication. The exact sentence
**`World asset download is not implemented yet.`**
is asserted by the test suite; don't rewrite it casually (CLI wrapping, test
and CI depend on it).

### 3.2 Guest-entry mode (explicit opt-in, `--guest-entry`)

Off by default; the default `NotImplementedDeliveryResolver` behavior is
unchanged. When the flag is passed, the resolver
(`src/ClusterMapDownloader/GuestEntry/GuestEntryDeliveryResolver.cs`)
reproduces the normal public client guest flow for **one specified world**
(full evidence: [RESEARCH.md §8](RESEARCH.md#8-guest-entry-mode-documented-explicit-opt-in)):

| Component | Role |
|---|---|
| `FirebaseApiKeyLocator` | locate the public web API key: `--firebase-api-key`, or scan `--il2cpp-dump`/`stringliteral.json`, or scan the package (AIza-shaped literals in `*_Data/resources.assets` — the observed carrier — plus `global-metadata.dat`); >1 distinct value ⇒ refuse to guess |
| `ClientPackageLocator` | auto-discover a local unpacked client package (explicit dir wins): working-tree `.work/client-package`, then registry-declared cluster install locations (HKCU/HKLM uninstall keys incl. WOW6432Node whose name/display name matches "cluster"), then well-known install dirs; every candidate is verified against the Unity data files above, so no candidate is trusted before verification |
| `GuestEntryResolverFactory` | single construction point for CLI + GUI: resolves package/dump defaults (`.work/...` conventions), returns null ("report options instead") when no key and nothing discoverable |
| `FirebaseGuestAuthenticator` | `POST identitytoolkit/v3/relyingparty/signupNewUser` `{"returnSecureToken":true}` → idToken (in memory only; errors masked, refusals → exit 5) |
| `RoomEntryApiClient` | `POST /v1/login` (Bearer idToken, empty JSON) → anonymous `UserPrivateResponse`; sets `X-Cluster-LoginID`; `POST /v1/world_room_set/{worldId}/entry_request` → `{roomId, deepLink}`; `POST /v1/rooms/{roomId}/enter_v2` `{"metricsPayload":"…"}` → `RoomEnterResponse.venue.platformResources` (desktop-style `Win` device/platform headers throughout — room routes gate on platform) |
| `GuestEntryDeliveryResolver` | map `assets[]`/`mainScene`/`subScenes[]` → `AssetDownload` list (`bundles/asset-NN-…`, `bundles/mainScene-00-…`, `bundles/subScene-NN-…`, deduped by URL); `SecretMasker` on every message |

Guarantees: no accounts/credential impersonation (anonymous guest sign-up
only), no fabricated device metrics (the `metricsPayload` self-identifies
the tool; one empty-string retry on HTTP 400), tokens never persisted or
logged, cluster 4xx refusals map to `RequiresNonPublicAccess` (exit 5) with
a masked reason, `AesAssetDecryptor`-encrypted bundles are saved as-is, and
the single-world/no-enumeration scope is enforced by the flow itself.

### 3.3 Exit codes

| code | meaning |
|---|---|
| 0 | success |
| 1 | generic error |
| 2 | download not implemented (default, no-flag delivery boundary), always with the final message above |
| 3 | usage error |
| 4 | world not publicly accessible (`privacyType` ≠ Public) |
| 5 | guest-entry delivery refused beyond the anonymous guest boundary — reported with a masked reason, not bypassed |

## 4. PowerShell support (`scripts/`)

Every script: `[CmdletBinding()]`, UTF-8 console, `Set-StrictMode`, module
import from `scripts/lib/ClusterMapDownloaderSupport.psm1`.

| Script | Purpose |
|---|---|
| `bootstrap.ps1` | dotnet SDK check, build, publish CLI (`artifacts-local/cli`), opt-in tools fetch (`-WithTools`: innounp + IFPSTools.NET + wrapper build; `-WithAnalysisTools`: Il2CppDumper), `-RunTests` |
| `download-world.ps1` | main entry point: ensures the CLI is published, maps parameters to CLI args (`-GuestEntry` opt-in, key never echoed when printing the command line), mirrors exit code, guarantees the not-implemented message on 2 |
| `convert-bundle.ps1` | bundle → GLB conversion (see RESEARCH.md §9, README §7): UnityFS magic gate, opt-in `-FetchTool` runtime fetch of AssetRipper, headless WebUI drive (LoadFile → Export/PrimaryContent), in-place normals repair of each scene GLB via `GlbTidier`/`tidy-glb` (auto-published CLI; honest skip when no dotnet SDK), deliverables copied under `output/<map name or worldId>/converted/` (scene GLB named `<map name>.glb`) with sha256 manifest (incl. `tidy` section) + personal-use notices; encrypted files skipped honestly (exit 2) |
| `extract-installer.ps1` | innounp extraction of the user's installer; `-Disassemble` (PascalScript bytecode); `-DownloadClientPackage` (the anonymous `update.cluster.mu` 7z fetch documented in RESEARCH.md); `-ExtractClientPackage` (7-Zip → `.work/client-package`) |
| `analyze-client.ps1` | file inventory, optional `-Il2CppDump` (Il2CppDumper), chunked binary pattern scans and text scans (`dump.cs`, `stringliteral.json`, `catalog.json`), all masked, into `.work/analysis/client-analysis-report.md` |
| `check-repo-policy.ps1` | repo-policy verification: tracked-file rules, `secret-shape` pattern scan, `.gitignore` coverage |
| `lib/ClusterMapDownloaderSupport.psm1` | everything above's shared plumbing: path roots, download/archive, third-party fetchers (innounp, IFPSTools.NET + csproj patch, Il2CppDumper), `Invoke-FileScan` (4 MiB overlapping-window regex scanner), `Find-AsciiStringsInFile`, `Find-TextLineMatches`, PowerShell parse checks, secret masking |

### 4.1 Why scanning is chunked

`GameAssembly.dll` is ~177 MiB; naive string dumps of that blow up memory.
`Invoke-FileScan` reads the file in windows (default 4 MiB + a 256-byte
carry) so a match spanning a window boundary is neither missed nor reported
twice, and reports stay capped per pattern. Output is secret-masked by
`Hide-SecretLikeValues` (the client binary embeds public-key value classes
that are *not* to be copied).

## 5. Boundaries (load-bearing, don't truck with them casually)

1. **No authentication or access-control bypass.** No credential extraction
   or token theft. When a route needs a token (e.g. the room-enter flow, see
   [RESEARCH.md §3.4](RESEARCH.md#34-the-authentication-wall-verified-live)),
   the default (no-flag) run stops and says so. The only exception is the
   explicit, documented `--guest-entry` mode, which reproduces the normal
   anonymous guest flow (the equivalence chain a fresh free client performs
   end to end) — no accounts, no impersonation, no crafted requests; see
   §3.2 and [RESEARCH.md §8](RESEARCH.md#8-guest-entry-mode-documented-explicit-opt-in).
2. **Repository contents are redistributable only.** Cluster client files,
   Unity/world assets, installer artefacts, dumps, signed URLs, tokens:
   never committed (`.gitignore` + `check-repo-policy.ps1` enforce this).
3. **No key literals.** The client embeds public API-key *value classes*
   (Firebase/Stripe/OAuth). Documented as classes only; the pattern-masker
   enforces it. The guest-entry mode reads the Firebase web API key at
   runtime from user-provided sources and `SecretMasker` scrubs it from
   every message.
4. **CI performs no research downloads.** Only build/test/policy checks;
   see `.github/workflows/ci.yml`.
5. **No decryption work, ever.** `AesAssetDecryptor`-protected bundles are
   saved as encrypted bytes; key extraction stays inside the game client
   (out of scope forever). The pipeline labels each saved file
   (`fileFormat`: `unityfs-plain` / `unknown-probably-encrypted`, plus
   `sha256`) instead of transforming it.
6. **Auto-discovery is user-local and verified.** `ClientPackageLocator`
   scans only the user's own machine (in-repo conventions
   `.work/client-package`, registry uninstall entries the cluster installer
   itself wrote, well-known install dirs) and accepts a candidate only when
   it actually contains the Unity data files the scan consumes. It never
   touches the network and never trusts unverified paths. The GUI shares the
   same `GuestEntryResolverFactory` and the same policies as the CLI (single
   world, in-memory tokens, masked messages, honest refusals).

## 6. Testing

* All fixtures synthetic; the sample metadata lives in
  [`../samples/world-metadata.sample.json`](../samples/world-metadata.sample.json)
  with obviously non-real values.
* HTTP behavior tested via a stub `HttpMessageHandler`.
* The not-implemented path is asserted verbatim (see `WorldDownloaderTests`).
* CI = build + test + PowerShell parse check + repo policy check; no
  client/asset download steps.

## 7. Extending the pipeline (design guide)

Adding a real `IWorldDeliveryResolver` is the *correct* extension point; the
rest of the pipeline already handles manifests and downloads. A resolver
must:

1. reuse the live metadata / venue values (never invent a URL);
2. assert the equivalence with the normal public client flow in code
   comments + docs ("what a normal client would do") — the guest-entry
   resolver is the reference implementation of this rule;
3. keep files local and scope-limited (single world, single output dir);
4. never fetch from endpoints that are only guessed into existence — the
   entry-request → enter_v2 chain is the only documented manifest source
   today;
5. map every refusal honestly (`RequiresNonPublicAccess`, exit 5) instead
   of trying credentials or workarounds.

## 8. Layout of the (git-ignored) runtime directories

| Directory | Contents |
|---|---|
| `.work/third-party/` | fetched tools: `innounp/`, `Il2CppDumper/`, `IFPSTools.NET/` (cloned + patched), `assetripper/` (+ its `logs/`; fetched only by `convert-bundle.ps1 -FetchTool`) |
| `.work/installer/` | innounp extraction of a user installer (`{app}`, `embedded`, `install_script.iss`) |
| `.work/client-package/` | 7-Zip extraction of `cluster_win.7z` (Unity client) |
| `.work/il2cpp-dump/` | Il2CppDumper output (`dump.cs`, `stringliteral.json`, `il2cpp.h`, `DummyDll/`) |
| `.work/analysis/` | `analyze-client.ps1` Markdown reports |
| `downloads/`, `output/`, `artifacts-local/`, `.temp/`, `cache/` | local outputs; all git-ignored |
