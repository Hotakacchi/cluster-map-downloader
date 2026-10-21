using System.Text.Json;
using System.Text.Json.Serialization;
using ClusterMapDownloader;

namespace ClusterMapDownloader.Cli;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch
        {
            // Console encoding may not be settable in some hosts; ignore.
        }

        if (args.Length == 0)
        {
            PrintHelp();
            return ExitCodes.Usage;
        }

        return args[0] switch
        {
            "download" => RunWorldCommand(args[1..], forceMetadataOnly: false),
            "metadata" => RunWorldCommand(args[1..], forceMetadataOnly: true),
            "version" => PrintVersion(),
            "help" => PrintHelp(outExit: true),
            var unknown when unknown.StartsWith('-') => UnknownOption(unknown),
            var unknown => UnknownCommand(unknown),
        };
    }

    private static int RunWorldCommand(string[] args, bool forceMetadataOnly)
    {
        string? worldUrl = null;
        string? output = null;
        string? appVersion = null;
        string? buildVersion = null;
        string? device = null;
        string? platform = null;
        string? apiBase = null;
        var metadataOnly = forceMetadataOnly;
        var dryRun = false;
        var json = false;
        var help = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-o":
                case "--output":
                    if (!TryNext(args, ref i, "output directory", ref output)) return ExitCodes.Usage;
                    break;
                case "--app-version":
                    if (!TryNext(args, ref i, "app version", ref appVersion)) return ExitCodes.Usage;
                    break;
                case "--build-version":
                    if (!TryNext(args, ref i, "build version", ref buildVersion)) return ExitCodes.Usage;
                    break;
                case "--device":
                    if (!TryNext(args, ref i, "device", ref device)) return ExitCodes.Usage;
                    break;
                case "--platform":
                    if (!TryNext(args, ref i, "platform", ref platform)) return ExitCodes.Usage;
                    break;
                case "--api-base":
                    if (!TryNext(args, ref i, "api base url", ref apiBase)) return ExitCodes.Usage;
                    break;
                case "--metadata-only":
                    metadataOnly = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--json":
                    json = true;
                    break;
                case "-h":
                case "--help":
                    help = true;
                    break;
                default:
                    if (arg.StartsWith('-'))
                    {
                        Console.Error.WriteLine($"Unknown option: {arg}");
                        PrintHelp();
                        return ExitCodes.Usage;
                    }
                    if (worldUrl is not null)
                    {
                        Console.Error.WriteLine($"Unexpected extra argument: {arg}");
                        PrintHelp();
                        return ExitCodes.Usage;
                    }
                    worldUrl = arg;
                    break;
            }
        }

        if (help)
        {
            PrintHelp(showWorldCommandUsage: true);
            return ExitCodes.Success;
        }

        if (worldUrl is null)
        {
            Console.Error.WriteLine("Missing world URL. Example:");
            Console.Error.WriteLine("  cluster-map-downloader download https://cluster.mu/w/<WORLD_ID> -o .\\output");
            return ExitCodes.Usage;
        }

        var apiOptions = new ClusterApiOptions();
        if (apiBase is not null) apiOptions = apiOptions with { BaseUrl = apiBase };
        if (appVersion is not null) apiOptions = apiOptions with { AppVersion = appVersion };
        if (buildVersion is not null) apiOptions = apiOptions with { BuildVersion = buildVersion };
        if (device is not null) apiOptions = apiOptions with { Device = device };
        if (platform is not null) apiOptions = apiOptions with { Platform = platform };

        var options = new WorldDownloadOptions
        {
            WorldUrl = worldUrl,
            OutputDirectory = output ?? Path.Combine(Environment.CurrentDirectory, "output"),
            MetadataOnly = metadataOnly,
            DryRun = dryRun,
            Api = apiOptions,
        };

        WorldDownloadResult result;
        try
        {
            result = WorldDownloader.RunAsync(options, ConsoleProgressNotifier.Instance)
                .GetAwaiter()
                .GetResult();
        }
        catch (WorldUrlFormatException)
        {
            return ExitCodes.Usage;
        }

        if (json)
        {
            PrintResultJson(result);
        }

        return result.ExitCode;
    }

    private static bool TryNext(string[] args, ref int i, string what, ref string? target)
    {
        if (i + 1 >= args.Length)
        {
            Console.Error.WriteLine($"Option '{args[i]}' needs a value ({what}).");
            target = null;
            return false;
        }
        target = args[++i];
        return true;
    }

    private static int PrintVersion()
    {
        Console.Out.WriteLine($"{ToolInfo.Name} {ToolInfo.Version}");
        Console.Out.WriteLine(ToolInfo.UnofficialNotice);
        Console.Out.WriteLine("License: MIT. Third-party notices: NOTICE.md");
        return ExitCodes.Success;
    }

    private static int PrintHelp(bool outExit = true, bool showWorldCommandUsage = true)
    {
        Console.Out.WriteLine($"""
            {ToolInfo.Name} v{ToolInfo.Version} — unofficial research tool (not affiliated with Cluster, Inc.)

            Usage:
              cluster-map-downloader download <world-url> [options]
              cluster-map-downloader metadata <world-url> [options]
              cluster-map-downloader version
              cluster-map-downloader help

            Commands:
              download   Parse the world URL, fetch public metadata, resolve the venue id,
                         then resolve the delivery mechanism. Research identified venue
                         AssetBundles as delivered only through cluster's authenticated
                         room-enter flow; emulating that is out of scope, so the tool
                         stops here (exit code 2). See docs/RESEARCH.md.
              metadata   Same as download, but stops after metadata/venue output.

            Options:
              -o, --output <dir>       Output directory (default .\output\<worldId> under CWD)
                  --metadata-only      Stop before delivery resolution (public metadata only)
                  --dry-run            Everything except writing files
                  --json               Print a machine-readable result object at the end
                  --app-version <v>    Override observed web-client X-Cluster-App-Version
                  --build-version <v>  Override observed web-client X-Cluster-Build-Version
                  --device <name>      Override X-Cluster-Device (default Web)
                  --platform <name>    Override X-Cluster-Platform (default Web)
                  --api-base <url>     Override the public API base URL
              -h, --help               Show this help

            Exit codes:
              0 success; 1 error; 2 world asset download not implemented yet;
              3 usage error; 4 world is not publicly accessible;
              5 delivery requires authorization the public flow does not provide.

            This tool implements no authentication or access-control bypass and keeps
            all downloaded content local. See README.md and NOTICE.md.
            """);
        if (showWorldCommandUsage)
        {
            Console.Out.WriteLine("""
                Examples:
                  cluster-map-downloader download https://cluster.mu/w/<WORLD_ID> -o .\output
                  cluster-map-downloader metadata https://cluster.mu/w/<WORLD_ID> --json
                """);
        }
        return outExit ? ExitCodes.Success : ExitCodes.Usage;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: '{command}'.");
        PrintHelp(outExit: false);
        return ExitCodes.Usage;
    }

    private static int UnknownOption(string option)
    {
        Console.Error.WriteLine($"Unknown option: '{option}' (options require a command first).");
        PrintHelp(outExit: false);
        return ExitCodes.Usage;
    }

    private static void PrintResultJson(WorldDownloadResult result)
    {
        var payload = new
        {
            tool = ToolInfo.Name,
            toolVersion = ToolInfo.Version,
            outcome = result.Outcome.ToString(),
            exitCode = result.ExitCode,
            success = result.Outcome == WorldDownloadOutcome.Success,
            worldId = result.WorldId,
            failureMessage = result.FailureMessage,
            warnings = result.Warnings,
            metadata = result.Metadata,
            venue = result.Venue is { } venue
                ? new { found = venue.Found, venueId = venue.VenueId, formatLooksValid = venue.FormatLooksValid, warnings = venue.Warnings }
                : null,
            delivery = result.Delivery is { } delivery
                ? new
                {
                    status = delivery.Status.ToString(),
                    reason = delivery.Reason,
                    nextResearchSteps = delivery.NextResearchSteps,
                }
                : null,
            files = result.Files,
        };
        Console.Out.WriteLine("--- result (json) ---");
        Console.Out.WriteLine(JsonSerializer.Serialize(payload, JsonOpts));
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
