# ifpsdasm-wrapper

A small .NET 8 console front-end for
[IFPSTools.NET](https://github.com/Wack0/IFPSTools.NET) (`IFPSLib`), used to
disassemble Inno Setup PascalScript bytecode such as the Cluster Windows
installer's `embedded/CompiledCode.bin`.

It exists because the legacy .NET Framework CLI that ships with IFPSTools.NET
often hits assembly-resolution failures on modern dependency versions
(`NativeMemoryArray`, `System.Runtime.CompilerServices.Unsafe`); targeting
`net8.0` and pulling the same packages via NuGet avoids that.

## Bootstrap

The referenced `IFPSLib` project is **not vendored** in this repository. Run:

```powershell
pwsh scripts/bootstrap.ps1 -WithTools
```

which clones IFPSTools.NET into `.work/third-party/IFPSTools.NET` (a local,
ignored directory) and builds:

```text
.work/tools/ifpsdasm-wrapper/ifpsdasm-wrapper.exe
```

## Usage

```text
ifpsdasm-wrapper <CompiledCode.bin> [-o <out.txt>] [--stats]
```

- default output: `<input>.dis.txt` next to the input file
- `--stats`: print file version and counts (types/functions/globals) only

The disassembly output stays local (`.work/`) and is never committed — see
`.gitignore` (`CompiledCode.bin`, `CompiledCode.dis.txt`, `install_script.iss`).

## License note

`ifpsdasm-wrapper` code in this repository is MIT (same as the project).
The underlying `IFPSLib` remains MIT-licensed by its authors; see NOTICE.md.
