# NOTICE

## Unofficial project

`cluster-map-downloader` is an **unofficial** research tool. It is **not**
affiliated with, endorsed by, or sponsored by **Cluster, Inc.** No Cluster
logos, branding, or proprietary assets are distributed as part of this
repository.

All client binaries (`cluster_win.exe`, `cluster.exe`, `GameAssembly.dll`,
`UnityPlayer.dll`, etc.), world data, and AssetBundles are downloaded,
extracted, and stored locally by whoever runs this project. They are **not**
committed to the repository and must not be redistributed.

## Third-party components

No third-party binaries or source code are vendored in this repository.
The following tools are downloaded and built locally by
`scripts/bootstrap.ps1` (`-WithTools` / `-WithAnalysisTools`) on demand and
remain in ignored working directories:

| Component | License | Source | Used for |
|---|---|---|---|
| [IFPSTools.NET](https://github.com/Wack0/IFPSTools.NET) (`IFPSLib`) | MIT (Copyright (c) 2022 zc; ifpscc/libifpscc derived from code Copyright (c) 2015 Jonson Tan) | cloned from upstream | Disassembling Inno Setup PascalScript bytecode (`embedded/CompiledCode.bin`) via `tools/ifpsdasm-wrapper`. Bootstrap patches `IFPSLib.csproj` (`System.Runtime.CompilerServices.Unsafe` 4.5.3 → 6.0.0, NU1605 fix) |
| [innounp 2.x](https://github.com/jrathlev/InnoUnpacker-Windows-GUI) | GNU GPL (fetched locally at runtime by `scripts/bootstrap.ps1` / `scripts/extract-installer.ps1`) | GitHub releases (`ui_2_2_13` build) | Extracting Inno Setup installers (`-x -d<dir> -y -m`: `-m` reaches `embedded/CompiledCode.bin`) |
| [7-Zip](https://www.7-zip.org/) | LGPL + unRAR restrictions (external executable, not fetched) | user-installed | Extracting `cluster_win.7z` client packages and opening `innounp` archives |
| [Cpp2IL](https://github.com/SamboyCoding/Cpp2IL) | MIT | GitHub releases (optional, fetched manually) | Alternative IL2CPP assembly analysis |
| [Il2CppDumper](https://github.com/Perfare/Il2CppDumper) v6.7.46 | MIT | GitHub releases (`Il2CppDumper-net6` zip; fetched by `-WithAnalysisTools`) | Dumping IL2CPP class/method surface from `GameAssembly.dll` + `global-metadata.dat` (`dotnet <dir>/Il2CppDumper.dll …`) |
| [AssetRipper](https://github.com/AssetRipper/AssetRipper) 2.0.0 (Free edition) | GNU GPL-3.0-or-later | GitHub releases zip (`AssetRipper_win_x64.zip`; fetched locally at runtime by `scripts/convert-bundle.ps1 -FetchTool` only) | Converting downloaded `unityfs-plain` world bundles into glTF 2.0 (GLB) scene models via its headless local WebUI (`POST /LoadFile` → `POST /Export/PrimaryContent`) |

If you reproduce the maintainers' documentation, keep the attributions above.
Do not commit any of these tools' binaries, their source, or any files they
generate from proprietary data to this repository.

## Cluster, Inc. content and services

- World metadata is fetched from the public cluster web API for **public
  worlds only** (see `docs/API.md`, `docs/api-findings.json`).
- This project implements no authentication bypass, access-control bypass,
  credential extraction, token theft, or similar behavior.
- By default the delivery stage stops at the no-authentication boundary
  (exit `2`). The explicit opt-in `--guest-entry` mode reproduces only the
  anonymous guest flow a fresh, free client performs for a single public
  world (no account login, no credential impersonation, no endpoint
  guessing); if cluster refuses that flow, the tool reports it (exit `5`)
  instead of working around it. The Windows GUI
  (`cluster-map-downloader-gui.exe`) is a front-end for the same flow and
  enforces the same policies.
- Some venue AssetBundles are AES-encrypted by the game client
  (`AesAssetDecryptor`). This tool saves those bytes as-is and implements
  no decryption or key extraction.
- Bundle conversion (`scripts/convert-bundle.ps1`) produces **generic model
  data only** (meshes/materials/textures/transforms) and always magic-checks
  for plain UnityFS first: encrypted or unrecognized files are skipped
  honestly, never decrypted. Converted GLB/textures are personal-use,
  local-only (`output\` is git-ignored), and repeated disclosures in the
  tool, viewer, and docs remind users that the 3D content's copyright stays
  with the world creators.
- World/AssetBundle content is copyrighted by its creators. Respect the
  [cluster Terms of Service](https://cluster.mu/terms) and applicable law;
  what you do with downloaded content is your responsibility.

## Cluster web API client headers

The public API requests made by this tool use non-secret web client version
strings (e.g. `X-Cluster-*: .../Web` constant values documented in
`docs/API.md`). These are facts of the public web API surface, not secrets.
