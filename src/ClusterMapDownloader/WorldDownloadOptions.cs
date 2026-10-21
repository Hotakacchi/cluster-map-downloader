namespace ClusterMapDownloader;

/// <summary>Options for one download run.</summary>
public sealed record WorldDownloadOptions
{
    /// <summary>World URL (cluster.mu form) or bare world id. Required.</summary>
    public required string WorldUrl { get; init; }

    /// <summary>Directory where files (metadata snapshot, assets, manifests) are written.</summary>
    public string OutputDirectory { get; init; } = Path.Combine(Environment.CurrentDirectory, "output");

    /// <summary>Fetch and print metadata only; skip delivery resolution and downloads.</summary>
    public bool MetadataOnly { get; init; }

    /// <summary>Run everything except writing files.</summary>
    public bool DryRun { get; init; }

    /// <summary>Public API options (headers, base URL).</summary>
    public ClusterApiOptions Api { get; init; } = new();

    /// <summary>Delivery resolver; defaults to the honest not-implemented resolver.</summary>
    public IWorldDeliveryResolver? DeliveryResolver { get; init; }

    /// <summary>Optional custom HTTP handler (tests); the pipeline creates its own client otherwise.</summary>
    public HttpMessageHandler? HttpHandler { get; init; }

    /// <summary>Optional per-file/byte progress sink (GUI); the pipeline reports null-free coarse progress otherwise.</summary>
    public IProgress<DownloadedFileProgress>? FileProgress { get; init; }
}

/// <summary>Final result of one download run.</summary>
public sealed record WorldDownloadResult
{
    public required WorldDownloadOutcome Outcome { get; init; }
    public string? FailureMessage { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public WorldMetadata? Metadata { get; init; }
    public VenueResolution? Venue { get; init; }
    public DeliveryResolution? Delivery { get; init; }
    public IReadOnlyList<DownloadedFile> Files { get; init; } = Array.Empty<DownloadedFile>();
    public required WorldDownloadOptions Options { get; init; }

    /// <summary>True when the run completed what was requested (including "not implemented" reports
    /// that completed all implemented stages).</summary>
    public bool IsTerminalSuccess => Outcome is WorldDownloadOutcome.Success
                                     or WorldDownloadOutcome.NotImplemented
                                     or WorldDownloadOutcome.RequiresNonPublicAccess;

    public int ExitCode => (int)Outcome;

    /// <summary>World id from the parsed URL, when parsing succeeded.</summary>
    public string? WorldId { get; init; }
}
