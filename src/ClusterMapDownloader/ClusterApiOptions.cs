namespace ClusterMapDownloader;

/// <summary>
/// Options for talking to the public cluster web API.
///
/// The default values reproduce the non-secret version strings observed in
/// the public cluster web client (October 2026). They are plain build
/// identifiers, not secrets, and can be overridden per run.
/// </summary>
public sealed record ClusterApiOptions
{
    /// <summary>Public web API base (trailing slash preserved by callers as needed).</summary>
    public const string DefaultBaseUrl = "https://api.cluster.mu/v1";

    /// <summary>Observed web-client app version string.</summary>
    public const string DefaultAppVersion = "3.103.2610071832";

    /// <summary>Observed web-client build version string.</summary>
    public const string DefaultBuildVersion = "2610141027";

    public string BaseUrl { get; init; } = DefaultBaseUrl;
    public string AppVersion { get; init; } = DefaultAppVersion;
    public string BuildVersion { get; init; } = DefaultBuildVersion;
    public string Device { get; init; } = "Web";
    public string Platform { get; init; } = "Web";
    /// <summary>Plain product token (comments are avoided because .NET splits them into separate values).</summary>
    public string UserAgent { get; init; } = $"cluster-map-downloader/{ToolInfo.Version}";

    /// <summary>The headers sent with every API request.</summary>
    public IReadOnlyDictionary<string, string> BuildHeaders()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Cluster-App-Version"] = AppVersion,
            ["X-Cluster-Build-Version"] = BuildVersion,
            ["X-Cluster-Device"] = Device,
            ["X-Cluster-Platform"] = Platform,
            ["Accept"] = "application/json",
            ["User-Agent"] = UserAgent,
        };
        return headers;
    }
}
