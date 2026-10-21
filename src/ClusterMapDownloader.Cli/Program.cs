using System.Text.Json;
using System.Text.Json.Serialization;
using ClusterMapDownloader;
using ClusterMapDownloader.GuestEntry;

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
        string? firebaseApiKey = null;
        string? clientPackage = null;
        string? il2CppDump = null;
        var metadataOnly = forceMetadataOnly;
        var dryRun = false;
        var json = false;
        var guestEntry = false;
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
                case "--firebase-api-key":
                    if (!TryNext(args, ref i, "Firebase web API key", ref firebaseApiKey)) return ExitCodes.Usage;
                    break;
                case "--client-package":
                    if (!TryNext(args, ref i, "client package directory", ref clientPackage)) return ExitCodes.Usage;
                    break;
                case "--il2cpp-dump":
                    if (!TryNext(args, ref i, "IL2CPP dump directory", ref il2CppDump)) return ExitCodes.Usage;
                    break;
                case "--guest-entry":
                    guestEntry = true;
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

        if (firebaseApiKey is not null && !guestEntry)
        {
            Console.Error.WriteLine("--firebase-api-key is only meaningful together with --guest-entry.");
            return ExitCodes.Usage;
        }

        if (guestEntry && metadataOnly)
        {
            Console.Error.WriteLine("--guest-entry cannot be combined with metadata-only mode " +
                                    "(guest entry exists to fetch the venue asset manifest).");
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

        if (guestEntry)
        {
            PrintGuestEntryNotice();
            options = options with
            {
                DeliveryResolver = new GuestEntryDeliveryResolver(
                    new GuestEntryOptions
                    {
                        FirebaseApiKey = firebaseApiKey,
                        ClientPackageDirectory = ResolveLocalDir(clientPackage, ".work/client-package"),
                        Il2CppDumpDirectory = ResolveLocalDir(il2CppDump, ".work/il2cpp-dump"),
                    }),
            };
        }

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

    /// <summary>Resolves a user-provided local directory or the conventional default when that exists.</summary>
    private static string? ResolveLocalDir(string? explicitValue, string conventionalDefault)
    {
        if (!string.IsNullOrWhiteSpace(explicitValue))
        {
            return explicitValue;
        }

        var path = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, conventionalDefault));
        return Directory.Exists(path) ? path : null;
    }

    private static void PrintGuestEntryNotice()
    {
        Console.Out.WriteLine("Guest-entry mode (explicit opt-in): this run reproduces the normal public client");
        Console.Out.WriteLine("guest flow for ONE specified world — anonymous Firebase guest sign-in (tokens in");
        Console.Out.WriteLine("memory only), world entry request, room-enter request, then the venue asset list");
        Console.Out.WriteLine("the client itself downloads.");
        Console.Out.WriteLine("  - no credentials are read, stored, or impersonated; nothing is decrypted;");
        Console.Out.WriteLine("  - single world, no enumeration; AES-encrypted bundles stay encrypted;");
        Console.Out.WriteLine("  - you are responsible for complying with cluster's terms of service and for");
        Console.Out.WriteLine("    respecting world creators' rights over their content.");
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
                         then resolve the delivery mechanism. By default the tool stops at
                         the no-authentication delivery boundary (exit code 2). The
                         documented opt-in mode --guest-entry instead reproduces the
                         normal public client guest flow (anonymous Firebase sign-in,
                         entry request, room-enter) to list and download that single
                         world's AssetBundles. See docs/RESEARCH.md and README.md.
              metadata   Same as download, but stops after metadata/venue output.

            Options:
              -o, --output <dir>       Output directory (default .\output\<worldId> under CWD)
                  --guest-entry        OPT-IN: reproduce the normal public client guest flow
                                       for this single world (no credentials, no decryption,
                                       no enumeration). Default OFF: download stops (exit 2).
                  --firebase-api-key <k>  Firebase web API key for guest entry (public, non-
                                       secret identifier; alternative to local auto-detect).
                                       Never logged, never committed from this tool.
                  --client-package <dir>  Local unpacked cluster client package used to
                                       auto-detect the web API key (global-metadata.dat scan).
                  --il2cpp-dump <dir>  Local IL2CPP dump dir (stringliteral.json) used to
                                       auto-detect the web API key.
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
