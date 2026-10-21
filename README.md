# cluster-map-downloader

An unofficial, open-source research project for downloading **publicly
accessible** [cluster.mu](https://cluster.mu/) world/map data without a
normal installation of the cluster desktop application.

> **Status: research complete; AssetBundle delivery available as an explicit
> opt-in mode** — public world metadata and venue-ID resolution are
> implemented and working. The final world AssetBundle delivery requires
> cluster's normal guest-authenticated room-enter flow (see
> [docs/RESEARCH.md](docs/RESEARCH.md)); by default the tool reports
> `2`/`World asset download is not implemented yet.` instead of faking it or
> bypassing anything. As a **documented, opt-in** flag (`--guest-entry`), the
> tool reproduces the normal public client guest flow (anonymous Firebase
> sign-in → entry request → room-enter → the venue asset list) for exactly
> one specified public world — no credentials, no decryption, no enumeration.

This project is **not affiliated with Cluster, Inc.** (see
[Legal / content notice](#legal--content-notice)).

## Status

| Stage | State |
|---|---|
| Parse `cluster.mu` world URL | **implemented** |
| Fetch public world metadata (web API) | **implemented** (tested against public worlds) |
| Resolve internal venue ID | **implemented** |
| Determine normal client-side delivery mechanism | **research complete** — identified: guest-authenticated room-enter flow (`RoomVenue.platformResources`); no public unauthenticated endpoint exists |
| Download Windows world / AssetBundle data | **opt-in implemented** (`--guest-entry` reproduces the normal client guest flow; default stays off with exit `2`) |
| Save files locally + print metadata/download info | **implemented** |

Interim behavior: when the delivery stage cannot be resolved, the tool
performs all metadata stages and then prints:

```text
World asset download is not implemented yet.
```

It does **not** fake a completed download.

## Features

- Worlds API client for the public cluster web API with the observed
  web-client headers (`X-Cluster-*`).
- Robust URL parsing (`https://cluster.mu/w/<WORLD_ID>`, mirrors, or the
  bare world ID).
- Venue resolution and metadata reporting (venue ID, venue size, world
  type, privacy type, SDK type/version, creator, thumbnails).
- Generic, tested asset download/manifest plumbing (used by the opt-in mode
  and ready for future resolvers); every downloaded file is classified by its
  leading bytes (`fileFormat`: `unityfs-plain` vs `unknown-probably-encrypted`)
  and hashed (`sha256`, manifest integrity column) — plain labeling only,
  never decryption.
- A paste-a-URL Windows GUI (`cluster-map-downloader-gui.exe`, Japanese UI):
  enter the world URL, press ダウンロード, watch byte-level progress; the
  public client-shipped identifier is auto-discovered from an installed
  cluster client, tokens stay in memory, and refusal outcomes are reported
  honestly (no circumvention).
- Reusable PowerShell bootstrap / installer-extraction / binary-analysis
  scripts for the research workflow.
- `ifpsdasm-wrapper`, a small .NET 8 tool that turns the Cluster installer's
  `CompiledCode.bin` (Inno Setup PascalScript bytecode) into readable
  disassembly.

## How it works

```text
world URL ──parse──► worldId
   worldId ──GET https://api.cluster.mu/v1/worlds/{worldId}──► metadata (venueId, venueSize, ...)
   venueId ──venue resolution──► venue_<24+ hex>
   venue   ──delivery resolution──► default: STOPS (exit 2): the identified delivery mechanism
            │                          requires cluster's guest-authenticated room-enter flow
            │                          (RoomVenue.platformResources) — not run without opt-in
            │   --guest-entry ► venue assets via the normal client guest flow:
            │        anonymous Firebase sign-in (public identitytoolkit REST,
            │        per-run key, tokens in memory only) →
            │        POST /v1/world_room_set/{worldId}/entry_request → roomId →
            │        POST /v1/rooms/{roomId}/enter_v2 → RoomEnterResponse.venue.platformResources
            └──downloader (generic HTTP file pipeline, manifest writing)──► output/<worldId>/...
```

The key open research question is answered in
[`docs/RESEARCH.md`](docs/RESEARCH.md): a public `worldId` resolves to a
`venueId` (e.g. `venue_7b06e33a0b3cec1e5fcaf5b028`), but the venue bundle
manifest (`RoomVenue.platformResources`) is only issued by cluster's
guest-authenticated room-enter flow (`POST /v1/rooms/{roomId}/enter_v2` /
`GET /v1/rooms/{roomId}/enter_info`, `401 BAD_TOKEN_EXIST` without a
Firebase `Authorization` token). The client then loads each
`assetBundleUrl` via `VenueAssetBundleLoaderV2.BundleLoader.LoadAsync`
(UnityWebRequest), with some bundles AES-encrypted (`AesAssetDecryptor`).

This project implements **no** authentication bypass, access-control bypass,
credential extraction, or token theft. By default it does not even touch the
guest-authenticated flow; the opt-in `--guest-entry` mode reproduces exactly
the anonymous guest flow a fresh, free client install performs (no accounts,
no forged tokens, no encryption keys) and is documented in
[docs/RESEARCH.md §8](docs/RESEARCH.md#8-guest-entry-mode-documented-explicit-opt-in).
If a download requires authorization that the normal public flow does not
provide, the tool stops at that point and says so.

## Requirements

- Windows 10/11 (the project itself is cross-platform; the research
  artifacts being analyzed are Windows binaries). The GUI additionally
  requires Windows with .NET 8 Desktop Runtime available either directly or
  via a SDK install (the bootstrap publishes it locally; no installer).
- [.NET SDK 8.0](https://dotnet.microsoft.com/download) or a newer SDK
  (e.g. 9.x can build the `net8.0` targets).
- PowerShell 7 (`pwsh`) recommended; scripts are largely compatible with
  Windows PowerShell 5.1.
- `git` (used to fetch third-party libraries; nothing requires this repo to
  BE a git checkout for the library usage).
- Optional, for installer research: `7-Zip` (to open `innounp`'s `.rar`) and
  an internet connection to fetch `innounp` and `IFPSTools.NET` locally.

## Usage

### 1. Bootstrap (build + publish CLI and GUI)

```powershell
pwsh .\scripts\bootstrap.ps1
# optional extras for the installer/client research flow:
pwsh .\scripts\bootstrap.ps1 -WithTools          # innounp + IFPSTools.NET + ifpsdasm-wrapper
pwsh .\scripts\bootstrap.ps1 -WithAnalysisTools  # Cpp2IL + Il2CppDumper release zips
pwsh .\scripts\bootstrap.ps1 -RunTests           # also run the unit test suite
```

### 2. Download a world

```powershell
.\scripts\download-world.ps1 `
  -WorldUrl "https://cluster.mu/w/<WORLD_ID>" `
  -Output ".\output"
```

Switches:

```powershell
.\scripts\download-world.ps1 -WorldUrl "https://cluster.mu/w/<WORLD_ID>" -MetadataOnly
.\scripts\download-world.ps1 -WorldUrl "https://cluster.mu/w/<WORLD_ID>" -DryRun -Json
```

### 3. The AssetBundle stage — explicit opt-in (`--guest-entry`)

By default (no flags) the tool stops at the delivery boundary with exit `2`.
The opt-in mode reproduces the normal public client guest flow for **one
specified world** (anonymous Firebase sign-up, cluster anonymous-account
login, entry request, room-enter v2, then the venue asset list) and bundles
land in `output\<worldId>\bundles\`:

```powershell
# key supplied on the command line (a public web API identifier, never logged):
.\scripts\download-world.ps1 -WorldUrl "https://cluster.mu/w/<WORLD_ID>" `
    -GuestEntry -FirebaseApiKey "<AIza...>"

# or let the tool locate the key from your own local client artifacts:
.\scripts\download-world.ps1 -WorldUrl "https://cluster.mu/w/<WORLD_ID>" -GuestEntry

# explicit CLI form + documentation:
artifacts-local\cli\cluster-map-downloader.exe download https://cluster.mu/w/<WORLD_ID> --guest-entry
artifacts-local\cli\cluster-map-downloader.exe help
```

What this mode does / does not do (details:
[docs/RESEARCH.md §8](docs/RESEARCH.md#8-guest-entry-mode-documented-explicit-opt-in)):

* performs the **same request chain as a fresh, free client install** —
  anonymous Firebase sign-up (public identitytoolkit REST endpoint),
  cluster guest login, world entry request, room-enter v2, then downloads
  the bundle URLs the client itself fetches; **no** account log-in, **no**
  credential impersonation, **no** endpoint guessing, **no** bulk enumeration;
* the Firebase web API key (a client-shipped public identifier) comes from
  the CLI or from local scans of *your* client artifacts — never committed;
* tokens live in memory for the run only; `AesAssetDecryptor`-encrypted
  bundles are saved as encrypted bytes (no decryption, no key extraction);
* you are responsible for complying with cluster's Terms of Service and for
  respecting creators' rights (see [Legal](#legal--content-notice)).

### 4. GUI (Windows, paste-a-URL)

`bootstrap.ps1` also publishes a small WinForms GUI to
`artifacts-local\gui\cluster-map-downloader-gui.exe` (Japanese UI). It always
runs the explicit opt-in guest-entry flow described above — that is the GUI's
whole purpose — with the same policies: single world, in-memory tokens,
honest refusal handling, as-is encrypted bytes.

1. Launch `cluster-map-downloader-gui.exe`.
2. Paste `https://cluster.mu/w/<WORLD_ID>`.
3. Press **ダウンロード**; progress (per-file, byte-level) and a log are shown.
4. Files land in the chosen 保存先 (`output\<worldId>\bundles\` by default);
   the manifest lists `fileFormat` + `sha256` per file. **保存フォルダを開く**
   opens the world directory.

Automation/testing arguments: `cluster-map-downloader-gui.exe [url] [-o dir]
--auto-close` pre-fills the URL, starts immediately, and (with `--auto-close`)
exits when the run finishes. The public client-shipped Firebase identifier is
discovered automatically from the locally installed cluster client
(registry-declared install location / well-known dirs); there is nothing else
to configure.

### 5. CLI equivalent

```text
artifacts-local\cli\cluster-map-downloader.exe download https://cluster.mu/w/<WORLD_ID> -o .\output
artifacts-local\cli\cluster-map-downloader.exe metadata https://cluster.mu/w/<WORLD_ID>
artifacts-local\cli\cluster-map-downloader.exe download https://cluster.mu/w/<WORLD_ID> --metadata-only --json
artifacts-local\cli\cluster-map-downloader.exe download https://cluster.mu/w/<WORLD_ID> --guest-entry
artifacts-local\cli\cluster-map-downloader.exe version
```

Exit codes: `0` success, `1` error, `2` world asset download not
implemented (default, no-flag behavior), `3` usage error,
`4` world is not publicly accessible,
`5` delivery refused beyond the guest boundary (reported with the server's
masked reason, never bypassed).

### 6. Installer / client research scripts

```powershell
# Third-party tools (fetched at runtime into .work/third-party; never vendored)
pwsh .\scripts\bootstrap.ps1 -WithTools -WithAnalysisTools

# Extract a user-provided installer locally (you downloaded it yourself), then
# disassemble its PascalScript bytecode with the bundled IFPSLib wrapper:
.\scripts\extract-installer.ps1 -InstallerPath "C:\path\to\cluster_win.exe" -Disassemble

# Optional follow-ups (each opt-in; artifacts stay in ignored local dirs):
#   -DownloadClientPackage  fetch the anonymous cluster_win.7z the installer itself gets
#   -ExtractClientPackage   7-Zip it into .work\client-package (7-Zip must be installed)
.\scripts\extract-installer.ps1 -InstallerPath "C:\path\to\cluster_win.exe" `
    -Disassemble -DownloadClientPackage -ExtractClientPackage

# Inspect the extracted client package (file inventory, pattern scans,
# optional -Il2CppDump producing an Il2CppDumper dump; report in .work/analysis):
.\scripts\analyze-client.ps1 -Il2CppDump
```

All of these download only research artifacts into `downloads/` / `.work/`
(git-ignored). CI never runs them.

## Research / implementation status

Full narrative: [`docs/RESEARCH.md`](docs/RESEARCH.md); endpoints and probe
results: [`docs/API.md`](docs/API.md) and the machine-readable
[`docs/api-findings.json`](docs/api-findings.json).

Highlights:

- **Public world metadata API** — works for public worlds with
  web-client-style headers, no authentication.
- **Probed and closed routes** (October 2026):
  `GET /v1/venues/{id}` → 405 (also for a world GUID / `venue_…` id),
  `GET /v1/worlds/{venueId}` → 404,
  `GET /v1/rooms/{roomId}/enter_v2` → 405 (POST-only);
  `GET /v1/rooms/{roomId}/enter_info` → 401 `BAD_TOKEN_EXIST` without auth,
  `GET /v1/rooms/{roomId}` → 404 `UNREGISTERED_ROOM` (rooms are runtime
  sessions). Error envelope: `{"id","code","status","detail","meta"}`.
- **Delivery chain (desktop client, from the IL2CPP dump)** —
  `RimuldarApiClient` (room enter) → `RoomEnterResponse.venue` →
  `RoomVenue.platformResources` (per-asset `assetBundleUrl`) →
  `VenueAssetBundleLoaderV2.BundleLoader.LoadAsync` (UnityWebRequest) →
  optional `AesAssetDecryptor`. The manifest is **not** publicly reachable.
- **Installer / package** — `cluster_win.exe` is an Inno Setup 6.7.0
  bootstrap whose PascalScript bytecode (`embedded\CompiledCode.bin`,
  disassemblable with IFPSTools.NET via `tools/ifpsdasm-wrapper`) downloads
  `cluster_win.7z` **anonymously** from `update.cluster.mu`; the package is
  a Unity 6 IL2CPP client whose Addressables (`StreamingAssets/aa`) are
  baked-in preview assets, not world content.
- **Public web JavaScript** — searched for
  `AssetBundle`/`bundleUrl`/`downloadUrl`/`venueId`/`revision`/`storage`;
  no world delivery endpoint exists client-side.
- **Auth chain (IL2CPP verified)** — anonymous Firebase sign-up
  (`signupNewUser?key=…`, `{"returnSecureToken":true}`) →
  `Authorization: Bearer <idToken>` on every API request
  (`RimuldarApiHeaderProcessor.BuildBearerToken`); world entry =
  `POST /v1/world_room_set/{wrsId}/entry_request` → `{roomId, deepLink}` →
  `POST /v1/rooms/{roomId}/enter_v2` with
  `{"metricsPayload":"…"}` → `RoomEnterResponse`.
- **Guest chain verified live end-to-end (October 2026)** — with the
  `--guest-entry` opt-in the tool downloaded a real public world's venue
  bundle (118,100,853 bytes, plain `UnityFS`) into
  `output/<worldId>/bundles/` together with `downloads-manifest.json`;
  the verified chain adds `POST /v1/login` (anonymous account linking) and
  `X-Cluster-LoginID` + desktop-style `Win` platform headers on room routes
  (room routes refuse `'Web'` with `406 platform_not_supported`).

Boundary decision narrative + the opt-in guest-entry mode:
[`docs/RESEARCH.md`](docs/RESEARCH.md) §4 and §8.

## Known limitations

- With no flags, world AssetBundle download stops at the delivery boundary
  with exit `2` and the exact final message — the default stays at the
  no-authentication boundary by design.
- The opt-in `--guest-entry` mode reproduces only the anonymous guest flow:
  worlds that require accounts, event tickets, or invitations are refused by
  cluster (reported honestly with exit `5`, not worked around).
- Some venue bundles are AES-encrypted by the game client
  (`AesAssetDecryptor`); this tool saves those bytes as-is and never
  extracts decryption keys.
- Only worlds whose privacy type is public are supported; non-public worlds
  stop early with a clear message.
- API headers reflect the web client versions observed in October 2026 and
  can be overridden (`--app-version`, `--build-version`); they are plain
  version strings, not secrets.
- The tool intends no bypass features of any kind and will not add any.

## Legal / content notice

- This is an **unofficial** project, not affiliated with, endorsed by, or
  connected to Cluster, Inc. Do not use Cluster logos or branding for the
  project.
- World/AssetBundle content is copyrighted by its creators and belongs to
  them. Downloaded files stay on your machine; do not redistribute them
  unless you have the right to do so.
- Use the tool politely and lawfully: respect Cluster's Terms of Service,
  robots/rate limits, and server capacity. The project fetches only
  publicly accessible data and avoids unnecessary endpoint traffic.
- This repository must contain only redistributable code and documentation.
  `cluster_win.exe`, `cluster.exe`, `GameAssembly.dll`, `UnityPlayer.dll`,
  `cluster_Data/`, AssetBundles, downloaded world files, temporary signed
  URLs, cookies, tokens, credentials, and raw proprietary client dumps are
  never committed (see `.gitignore`).

## Development

```powershell
pwsh .\scripts\bootstrap.ps1 -RunTests
dotnet test ClusterMapDownloader.sln
pwsh .\scripts\check-repo-policy.ps1   # verifies no forbidden artefacts are tracked
```

Project layout and pipeline internals:
[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).
A synthetic metadata schema sample lives at
[`samples/world-metadata.sample.json`](samples/world-metadata.sample.json);
tests must not embed a real copyrighted world's data.

## License

Original code: [MIT](LICENSE).
Third-party acknowledgements: [NOTICE.md](NOTICE.md).
