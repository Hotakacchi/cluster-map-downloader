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
                └──────────┬───────────────┘   Not implemented → exit 2 & final message
                           ▼
                ┌──────────────────────────┐
 assets   ────► │  FileDownloader          │  generic HTTP file pipeline
                └──────────┬───────────────┘
                           ▼
                  world-metadata.json / downloads-manifest.json / files
```

## 2. Projects

| Project | Purpose | Notes |
|---|---|---|
| `src/ClusterMapDownloader` | .NET 8 class library: URL parsing, API client, metadata models, venue resolution, download plumbing, orchestration | zero external network beyond one public endpoint |
| `src/ClusterMapDownloader.Cli` | `cluster-map-downloader` CLI (`download`, `metadata`, `version`, `help`) | exit-code contract documented below |
| `tests/ClusterMapDownloader.Tests` | xUnit suite (36 tests), all synthetic (no copyrighted world data) | see "Testing", §6 |
| `tools/ifpsdasm-wrapper` | net8.0 front-end for IFPSLib; turns the installer's `CompiledCode.bin` into readable disassembly | NOT part of the main solution — needs `.work/third-party/IFPSTools.NET` (bootstrap-fetched) |
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
| `NotImplementedDeliveryResolver` | always reports `NotImplemented`, with reason + next research steps (see below) |
| `FileDownloader` | staged downloads, byte formatting, resume-friendly manifest rows |
| `WorldDownloader` | the six-stage orchestrator; also writes `world-metadata.json` and `downloads-manifest.json` |
| `ExitCodes` | the CLI exit contract |
| `ToolInfo` | name/version/notice strings |

### 3.1 The delivery boundary (load-bearing)

`DeliveryStatus`:

| Status | CLI exit | Behavior |
|---|---|---|
| `Success` (future) | 0 | proceeds to `FileDownloader` |
| `NotImplemented` | **2** | final message printed, no files, research keywords shown |
| `RequiresNonPublicAccess` | 5 | same reporting, different classification |

The built-in resolver reports the state honestly with a reason and a list of
"next research steps"; one of them is a *hard no* — never emulate
authentication. The exact sentence
**`World asset download is not implemented yet.`**
is asserted by the test suite; don't rewrite it casually (CLI wrapping, test
and CI depend on it).

### 3.2 Exit codes

| code | meaning |
|---|---|
| 0 | success |
| 1 | generic error |
| 2 | download not implemented (delivery boundary), always with the final message above |
| 3 | usage error |
| 4 | world not publicly accessible (`privacyType` ≠ Public) |
| 5 | delivery needs non-public authorization — reported, not bypassed |

## 4. PowerShell support (`scripts/`)

Every script: `[CmdletBinding()]`, UTF-8 console, `Set-StrictMode`, module
import from `scripts/lib/ClusterMapDownloaderSupport.psm1`.

| Script | Purpose |
|---|---|
| `bootstrap.ps1` | dotnet SDK check, build, publish CLI (`artifacts-local/cli`), opt-in tools fetch (`-WithTools`: innounp + IFPSTools.NET + wrapper build; `-WithAnalysisTools`: Il2CppDumper), `-RunTests` |
| `download-world.ps1` | main entry point: ensures the CLI is published, maps parameters to CLI args, mirrors exit code, guarantees the not-implemented message on 2 |
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
   the tool stops and says so.
2. **Repository contents are redistributable only.** Cluster client files,
   Unity/world assets, installer artefacts, dumps, signed URLs, tokens:
   never committed (`.gitignore` + `check-repo-policy.ps1` enforce this).
3. **No key literals.** The client embeds public API-key *value classes*
   (Firebase/Stripe/OAuth). Documented as classes only; the pattern-masker
   enforces it.
4. **CI performs no research downloads.** Only build/test/policy checks;
   see `.github/workflows/ci.yml`.

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
rest of the pipeline already handles manifests, downloads and manifests. A
resolver must:

1. reuse the live metadata / venue values (never invent a URL);
2. assert the equivalence with the normal public client flow in code
   comments + docs ("what a normal client would do");
3. keep files local and scope-limited (single world, single output dir);
4. never fetch from unauthenticated endpoints that are only guessed into
   existence — the room-enter flow is the only documented manifest source
   today.

## 8. Layout of the (git-ignored) runtime directories

| Directory | Contents |
|---|---|
| `.work/third-party/` | fetched tools: `innounp/`, `Il2CppDumper/`, `IFPSTools.NET/` (cloned + patched) |
| `.work/installer/` | innounp extraction of a user installer (`{app}`, `embedded`, `install_script.iss`) |
| `.work/client-package/` | 7-Zip extraction of `cluster_win.7z` (Unity client) |
| `.work/il2cpp-dump/` | Il2CppDumper output (`dump.cs`, `stringliteral.json`, `il2cpp.h`, `DummyDll/`) |
| `.work/analysis/` | `analyze-client.ps1` Markdown reports |
| `downloads/`, `output/`, `artifacts-local/`, `.temp/`, `cache/` | local outputs; all git-ignored |
