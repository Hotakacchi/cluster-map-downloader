namespace ClusterMapDownloader;

/// <summary>
/// Flow outcomes of a download run, mapped to process exit codes by the CLI.
/// </summary>
public enum WorldDownloadOutcome
{
    /// <summary>Everything requested was completed.</summary>
    Success = 0,

    /// <summary>An unexpected error occurred (network, API, IO, cancellation).</summary>
    Error = 1,

    /// <summary>The world asset delivery mechanism is not implemented yet.</summary>
    NotImplemented = 2,

    /// <summary>The command line / requested URL could not be interpreted.</summary>
    Usage = 3,

    /// <summary>The world is not publicly accessible (privacy type not "Public").</summary>
    NotPublic = 4,

    /// <summary>Downloading would require authorization that the normal public flow does not provide.</summary>
    RequiresNonPublicAccess = 5,
}

/// <summary>Map from <see cref="WorldDownloadOutcome"/> to process exit codes.</summary>
public static class ExitCodes
{
    public const int Success = (int)WorldDownloadOutcome.Success;
    public const int Error = (int)WorldDownloadOutcome.Error;
    public const int NotImplemented = (int)WorldDownloadOutcome.NotImplemented;
    public const int Usage = (int)WorldDownloadOutcome.Usage;
    public const int NotPublic = (int)WorldDownloadOutcome.NotPublic;
    public const int RequiresNonPublicAccess = (int)WorldDownloadOutcome.RequiresNonPublicAccess;
}
