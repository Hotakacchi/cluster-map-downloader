using System.Text;
using IFPSLib;

namespace IfpsDasmWrapper;

/// <summary>
/// ifpsdasm-wrapper:
/// converts an Inno Setup PascalScript bytecode file (typically
/// <c>embedded/CompiledCode.bin</c>) into readable disassembly using the MIT
/// licensed <see href="https://github.com/Wack0/IFPSTools.NET">IFPSLib</see>
/// library. This is a modern net8.0 front-end; the legacy .NET Framework CLI
/// shipping with IFPSTools.NET often fails on newer dependency versions.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        string? input = null;
        string? output = null;
        var statsOnly = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-o":
                case "--output":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine($"Option '{arg}' needs a file path.");
                        PrintUsage();
                        return 3;
                    }
                    output = args[++i];
                    break;
                case "--stats":
                    statsOnly = true;
                    break;
                case "-h":
                case "--help":
                    PrintUsage();
                    return 0;
                default:
                    if (arg.StartsWith('-'))
                    {
                        Console.Error.WriteLine($"Unknown option: {arg}");
                        PrintUsage();
                        return 3;
                    }
                    if (input is not null)
                    {
                        Console.Error.WriteLine($"Unexpected extra argument: {arg}");
                        PrintUsage();
                        return 3;
                    }
                    input = arg;
                    break;
            }
        }

        if (input is null)
        {
            PrintUsage();
            return 3;
        }

        if (!File.Exists(input))
        {
            Console.Error.WriteLine($"Input file not found: {input}");
            return 1;
        }

        try
        {
            using var stream = File.OpenRead(input);
            var script = Script.Load(stream);

            if (statsOnly)
            {
                Console.Out.WriteLine(
                    $"file={Path.GetFullPath(input)} version={script.FileVersion} " +
                    $"types={script.Types.Count} functions={script.Functions.Count} " +
                    $"globals={script.GlobalVariables.Count} entry={(script.EntryPoint?.Name ?? "(none)")}");
                return 0;
            }

            var disOutput = output ??= Path.ChangeExtension(input, ".dis.txt");
            var disassembly = script.Disassemble();
            File.WriteAllText(disOutput, disassembly, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            Console.Out.WriteLine($"Disassembled '{input}'");
            Console.Out.WriteLine($"            -> '{disOutput}'");
            Console.Out.WriteLine(
                $"   (version={script.FileVersion}, types={script.Types.Count}, " +
                $"functions={script.Functions.Count}, globals={script.GlobalVariables.Count})");
            Console.Out.WriteLine();
            Console.Out.WriteLine(
                "Next: search the output for delivery-related tokens (http, https, cluster.mu, update.cluster.mu, " +
                "manifest, package, archive, version, UnityPlayer, GameAssembly, Addressables, URL).");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to disassemble '{input}': {ex.Message}");
            return 1;
        }
    }

    private static void PrintUsage()
    {
        Console.Out.WriteLine("""
            ifpsdasm-wrapper — Inno Setup PascalScript (ROPS) disassembler front-end for IFPSLib.

            Usage:
              ifpsdasm-wrapper <CompiledCode.bin> [-o | --output <out.txt>] [--stats]

            Input:  Inno Setup "CompiledCode.bin" bytecode (RemObjects PascalScript).
            Output: disassembly text next to the input (default: <input>.dis.txt).

            The tool only reads the file you give it. Extract the bytecode yourself,
            e.g. with innounp (scripts/extract-installer.ps1 automates this locally).

            Built on IFPSTools.NET (MIT) — https://github.com/Wack0/IFPSTools.NET
            """);
    }
}
