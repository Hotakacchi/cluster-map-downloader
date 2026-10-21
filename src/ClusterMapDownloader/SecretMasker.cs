using System.Text.RegularExpressions;

namespace ClusterMapDownloader;

/// <summary>
/// Centralizes masking of secret-shaped strings so no credential literal can leak
/// into console output, logs, manifests or exception messages.
///
/// The tool never stores tokens or keys and never prints them; anything that
/// could carry such a value (server error text, exception messages) is scrubbed
/// through <see cref="Mask"/> before display.
/// </summary>
public static partial class SecretMasker
{
    /// <summary>Firebase web API keys are always AIza… (35 chars after the prefix).</summary>
    [GeneratedRegex("AIza[0-9A-Za-z_\\-]{20,}")]
    private static partial Regex FirebaseApiKeyPattern();

    /// <summary>JWT-shaped strings (Firebase idToken etc.): eyJ…header.payload.signature.</summary>
    [GeneratedRegex("eyJ[A-Za-z0-9_\\-]{5,}\\.[A-Za-z0-9_\\-]{5,}\\.[A-Za-z0-9_\\-]{5,}")]
    private static partial Regex JwtPattern();

    /// <summary>Firebase refresh tokens observed as 8.4+ hex-ish segments.</summary>
    [GeneratedRegex("\\b[A-Za-z0-9]{8}-[A-Za-z0-9_\\-]{100,}\\b")]
    private static partial Regex RefreshTokenPattern();

    /// <summary>Replaces any secret-shaped substring with a short, non-reversible stub.</summary>
    public static string Mask(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var result = FirebaseApiKeyPattern().Replace(input, static m => MaskToken(m.Value, "AIza"));
        result = RefreshTokenPattern().Replace(result, static _ => "<refresh-token>");
        return JwtPattern().Replace(result, static _ => "<jwt>");
    }

    /// <summary>True when the value has a shape that must not be emitted or persisted.</summary>
    public static bool LooksLikeFirebaseApiKey(string? value) => value is not null && FirebaseApiKeyPattern().IsMatch(value);

    /// <summary>Masks the middle of a token but keeps a deterministic, tiny tail for debugging.</summary>
    public static string MaskToken(string value, string visiblePrefix)
        => value.Length <= visiblePrefix.Length + 4
            ? "<masked>"
            : $"{visiblePrefix}…<masked:{value[^4..]}>";
}
