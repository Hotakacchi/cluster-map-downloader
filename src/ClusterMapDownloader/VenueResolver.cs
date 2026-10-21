using System.Text.RegularExpressions;

namespace ClusterMapDownloader;

/// <summary>
/// Result of resolving the internal venue identifier from world metadata.
/// </summary>
/// <param name="Found">True when metadata carried a non-empty <c>venueId</c>.</param>
/// <param name="VenueId">The raw venue id value, e.g. <c>venue_7b06e33a0b3cec1e5fcaf5b028</c>.</param>
/// <param name="FormatLooksValid">True when the value matches the observed <c>venue_&lt;hex&gt;</c> shape.</param>
/// <param name="Warnings">Human-readable warnings (format drift, etc.).</param>
public sealed record VenueResolution(
    bool Found,
    string? VenueId,
    bool FormatLooksValid,
    IReadOnlyList<string> Warnings)
{
    public static VenueResolution NotFound() =>
        new(false, null, false, Array.Empty<string>());
}

/// <summary>
/// Resolves the internal venue id from world metadata. This is the value the
/// native client is expected to send when loading a world.
/// </summary>
public static partial class VenueResolver
{
    [GeneratedRegex("^venue_[0-9a-fA-F]+$")]
    private static partial Regex VenueIdShape();

    public static VenueResolution Resolve(WorldMetadata metadata)
    {
        var venueId = metadata.VenueId;

        if (string.IsNullOrWhiteSpace(venueId))
        {
            return new VenueResolution(
                Found: false,
                VenueId: null,
                FormatLooksValid: false,
                Warnings: new[]
                {
                    "World metadata did not contain a 'venueId'. The world may be a special/external world type " +
                    "or the metadata shape has changed.",
                });
        }

        var warnings = new List<string>();
        var valid = VenueIdShape().IsMatch(venueId);
        if (!valid)
        {
            warnings.Add(
                $"Venue id '{venueId}' does not match the observed 'venue_<hex>' format; keep this in mind when " +
                "checking delivery logic.");
        }

        return new VenueResolution(
            Found: true,
            VenueId: venueId,
            FormatLooksValid: valid,
            Warnings: warnings);
    }
}
