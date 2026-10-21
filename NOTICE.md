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

If you reproduce the maintainers' documentation, keep the attributions above.
Do not commit any of these tools' binaries, their source, or any files they
generate from proprietary data to this repository.

## Cluster, Inc. content and services

- World metadata is fetched from the public cluster web API for **public
  worlds only** (see `docs/API.md`, `docs/api-findings.json`).
- This project implements no authentication bypass, access-control bypass,
  credential extraction, token theft, or similar behavior.
- Where a download requires authorization that the normal public flow does
  not provide, the tool stops and reports the limitation instead of
  attempting to work around it.
- World/AssetBundle content is copyrighted by its creators. Respect the
  [cluster Terms of Service](https://cluster.mu/terms) and applicable law.

## Cluster web API client headers

The public API requests made by this tool use non-secret web client version
strings (e.g. `X-Cluster-*: .../Web` constant values documented in
`docs/API.md`). These are facts of the public web API surface, not secrets.
