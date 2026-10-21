# cluster-map-downloader

An unofficial, open-source research project for downloading **publicly
accessible** [cluster.mu](https://cluster.mu/) world/map data without a
normal installation of the cluster desktop application.

> **Status: research complete; download stage intentionally stops at the
> no-auth boundary** — public world metadata and venue-ID resolution are
> implemented and working; the final world AssetBundle delivery turns out
> to require cluster's authenticated room-enter flow (see
> [docs/RESEARCH.md](docs/RESEARCH.md)), so this tool reports
> `2`/`World asset download is not implemented yet.` instead of faking it
> or bypassing anything.

This project is **not affiliated with Cluster, Inc.** (see
[Legal / content notice](#legal--content-notice)).

## Status

| Stage | State |
|---|---|
| Parse `cluster.mu` world URL | **implemented** |
| Fetch public world metadata (web API) | **implemented** (tested against public worlds) |
| Resolve internal venue ID | **implemented** |
| Determine normal client-side delivery mechanism | **research complete** — identified: authenticated room-enter flow (`RoomVenue.platformResources`); no public unauthenticated endpoint exists, so it is deliberately **not implemented** |
| Download Windows world / AssetBundle data | **not implemented** (by design at the no-auth boundary) |
| Save files locally + print metadata/download info | **implemented** (wired; becomes reachable only if an honest `IWorldDeliveryResolver` is ever added) |

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
- Generic, tested asset download/manifest plumbing ready for use once the
  delivery mechanism is identified.
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
   venue   ──delivery resolution──► STOPS (exit 2): the identified delivery mechanism
           │                          requires cluster's authenticated room-enter flow
           │                          (RoomVenue.platformResources) — not emulated here
           └──downloader (generic HTTP file pipeline, manifest writing)──► output/<worldId>/...
```

The key open research question is answered in
[`docs/RESEARCH.md`](docs/RESEARCH.md): a public `worldId` resolves to a
`venueId` (e.g. `venue_7b06e33a0b3cec1e5fcaf5b028`), but the venue bundle
manifest (`RoomVenue.platformResources`) is only issued by cluster's
authenticated room-enter flow (`POST /v1/rooms/{roomId}/enter_v2` /
`GET /v1/rooms/{roomId}/enter_info`, `401 BAD_TOKEN_EXIST` without a
Firebase `Authorization` token). The client then loads each
`assetBundleUrl` via `VenueAssetBundleLoaderV2.BundleLoader.LoadAsync`
(UnityWebRequest), with some bundles AES-encrypted (`AesAssetDecryptor`).

This project deliberately implements **no** authentication bypass,
access-control bypass, credential extraction, or token theft; it also does
not emulate a client session to obtain that manifest. If a download
requires authorization that the normal public flow does not provide, the
tool stops at that point and says so.

## Requirements

- Windows 10/11 (the project itself is cross-platform; the research
  artifacts being analyzed are Windows binaries).
- [.NET SDK 8.0](https://dotnet.microsoft.com/download) or a newer SDK
  (e.g. 9.x can build the `net8.0` targets).
- PowerShell 7 (`pwsh`) recommended; scripts are largely compatible with
  Windows PowerShell 5.1.
- `git` (used to fetch third-party libraries; nothing requires this repo to
  BE a git checkout for the library usage).
- Optional, for installer research: `7-Zip` (to open `innounp`'s `.rar`) and
  an internet connection to fetch `innounp` and `IFPSTools.NET` locally.

## Usage

### 1. Bootstrap (build + publish CLI)

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

### 3. CLI equivalent

```text
artifacts-local\cli\cluster-map-downloader.exe download https://cluster.mu/w/<WORLD_ID> -o .\output
artifacts-local\cli\cluster-map-downloader.exe metadata https://cluster.mu/w/<WORLD_ID>
artifacts-local\cli\cluster-map-downloader.exe download https://cluster.mu/w/<WORLD_ID> --metadata-only --json
artifacts-local\cli\cluster-map-downloader.exe version
```

Exit codes: `0` success, `1` error, `2` world asset download not
implemented, `3` usage error, `4` world is not publicly accessible,
`5` delivery requires non-public authorization (reported, not bypassed).

### 4. Installer / client research scripts

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

Next steps and the boundary decision:
[`docs/RESEARCH.md`](docs/RESEARCH.md) §4.

## Known limitations

- World AssetBundle download is not implemented — **by design**: the
  identified delivery mechanism requires cluster's authenticated room-enter
  flow, which this tool will not emulate. It prints the exact final message
  and exits `2`.
- Only worlds whose privacy type is public are supported; non-public worlds
  stop early with a clear message.
- API headers reflect the web client versions observed in October 2026 and
  can be overridden (`--app-version`, `--build-version`); they are plain
  version strings, not secrets.
- The tool intends no bypass features of any kind and will not add any.
- The builtin delivery resolver always reports *not implemented*; a future
  public, normally-authorized delivery path would need the documented
  design process in [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) §7.

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
