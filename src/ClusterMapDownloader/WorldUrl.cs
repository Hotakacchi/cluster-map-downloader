using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace ClusterMapDownloader;

/// <summary>Raised when a world URL/ID cannot be parsed.</summary>
public sealed class WorldUrlFormatException(string message) : Exception(message);

/// <summary>
/// A parsed cluster.mu world URL and its world ID.
/// Supported inputs (case-insensitive, optional scheme):
/// <list type="bullet">
///   <item><c>https://cluster.mu/w/&lt;worldId&gt;</c></item>
///   <item><c>http://www.cluster.mu/w/&lt;worldId&gt;</c> (and other <c>www</c> variants)</item>
///   <item>optional locale segment: <c>https://cluster.mu/en/w/&lt;worldId&gt;</c></item>
///   <item>trailing path / query / fragment: <c>.../w/&lt;worldId&gt;/buy</c></item>
///   <item>a bare world id, e.g. <c>c59fd20a-85b9-45ad-a357-4fdece9c180b</c></item>
/// </list>
/// </summary>
public sealed record WorldUrl
{
    private static readonly Regex UrlPattern = new(
        @"^(?:https?:)?/{0,2}(?:www\.)?cluster\.mu(?:/[a-zA-Z]{2}(?:[-_][A-Za-z]{2,8})?)?/w/(?<id>[A-Za-z0-9][A-Za-z0-9_\-]{6,63})(?:[/?#].*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BareIdPattern = new(
        @"^[A-Za-z0-9][A-Za-z0-9_\-]{6,63}$", RegexOptions.Compiled);

    private static readonly Regex GuidLikePattern = new(
        @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
        RegexOptions.Compiled);

    /// <summary>Original input string.</summary>
    public string Raw { get; }

    /// <summary>World id as it appeared in the input.</summary>
    public string WorldId { get; }

    /// <summary>True if the world id looks like the observed GUID format.</summary>
    public bool IsGuidLike { get; }

    /// <summary>Canonical cluster.mu web page for this world.</summary>
    public string CanonicalUrl => $"https://cluster.mu/w/{WorldId}";

    private WorldUrl(string raw, string worldId)
    {
        Raw = raw;
        WorldId = worldId;
        IsGuidLike = GuidLikePattern.IsMatch(worldId);
    }

    /// <summary>Parses the input or throws <see cref="WorldUrlFormatException"/>.</summary>
    public static WorldUrl Parse(string? raw)
    {
        if (!TryParse(raw, out var url, out var error))
        {
            throw new WorldUrlFormatException(error ?? $"Not a cluster.mu world URL: '{raw}'.");
        }
        return url!;
    }

    /// <summary>
    /// Parses a cluster.mu world URL (with or without scheme) or a bare world id.
    /// </summary>
    public static bool TryParse(string? raw, [NotNullWhen(true)] out WorldUrl? worldUrl,
        [NotNullWhen(false)] out string? error)
    {
        worldUrl = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "World URL is empty. Expected e.g. https://cluster.mu/w/<WORLD_ID>.";
            return false;
        }

        raw = raw.Trim();

        var urlMatch = UrlPattern.Match(raw);
        if (urlMatch.Success)
        {
            worldUrl = new WorldUrl(raw, urlMatch.Groups["id"].Value);
            error = null;
            return true;
        }

        if (raw.Contains('/', StringComparison.Ordinal) || raw.Contains('\\', StringComparison.Ordinal) ||
            raw.Contains('?', StringComparison.Ordinal))
        {
            error = $"Not a cluster.mu world URL: '{raw}'. " +
                    "Expected e.g. https://cluster.mu/w/<WORLD_ID> (host cluster.mu with /w/ path segment).";
            return false;
        }

        if (BareIdPattern.IsMatch(raw))
        {
            worldUrl = new WorldUrl(raw, raw);
            error = null;
            return true;
        }

        error = $"Not a valid world id or cluster.mu world URL: '{raw}'. " +
                "Expected a world id containing only alphanumerics, dashes, or underscores " +
                "(GUID-like, e.g. c59fd20a-85b9-45ad-a357-4fdece9c180b).";
        return false;
    }

    public override string ToString() => CanonicalUrl;
}
