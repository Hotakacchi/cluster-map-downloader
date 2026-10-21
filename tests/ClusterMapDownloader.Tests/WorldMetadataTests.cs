using ClusterMapDownloader;
using Xunit;

namespace ClusterMapDownloader.Tests;

public class WorldMetadataTests
{
    [Fact]
    public void Parses_all_known_fields()
    {
        var metadata = System.Text.Json.JsonSerializer.Deserialize<WorldMetadata>(
            FixtureData.SampleWorldMetadataJson,
            WorldMetadataJson.Options);

        Assert.NotNull(metadata);
        Assert.Equal(FixtureData.SyntheticWorldId, metadata!.Id);
        Assert.Equal("Example Public World", metadata.Name);
        Assert.Equal(FixtureData.SyntheticVenueId, metadata.VenueId);
        Assert.Equal(234567890, metadata.VenueSize);
        Assert.Equal("Normal", metadata.WorldType);
        Assert.Equal("Public", metadata.PrivacyType);
        Assert.Equal("ClusterCreatorKit", metadata.SdkType);
        Assert.Equal("3.3.0", metadata.SdkVersion);
        Assert.Equal("Art", metadata.Category);
        Assert.True(metadata.CanEntry);
        Assert.Equal("example_creator_user", metadata.Creator!.Username);
        Assert.Equal("Example Creator", metadata.Creator.DisplayName);
    }

    [Fact]
    public void Preserves_unknown_fields_in_extension_data()
    {
        var metadata = System.Text.Json.JsonSerializer.Deserialize<WorldMetadata>(
            FixtureData.SampleWorldMetadataJson,
            WorldMetadataJson.Options);

        Assert.NotNull(metadata?.Extra);
        Assert.True(metadata!.Extra!.ContainsKey("someUnknownField"));
    }

    [Fact]
    public void Null_venue_fields_survive()
    {
        var metadata = System.Text.Json.JsonSerializer.Deserialize<WorldMetadata>(
            FixtureData.Json(venueId: null, privacyType: null),
            WorldMetadataJson.Options);

        Assert.NotNull(metadata);
        Assert.Null(metadata!.VenueId);
        Assert.Null(metadata.PrivacyType);
    }
}

public class VenueResolverTests
{
    [Fact]
    public void Resolves_valid_venue_id()
    {
        var metadata = System.Text.Json.JsonSerializer.Deserialize<WorldMetadata>(
            FixtureData.SampleWorldMetadataJson, WorldMetadataJson.Options)!;

        var venue = VenueResolver.Resolve(metadata);

        Assert.True(venue.Found);
        Assert.Equal(FixtureData.SyntheticVenueId, venue.VenueId);
        Assert.True(venue.FormatLooksValid);
        Assert.Empty(venue.Warnings);
    }

    [Fact]
    public void Flags_malformed_venue_id()
    {
        var metadata = System.Text.Json.JsonSerializer.Deserialize<WorldMetadata>(
            FixtureData.Json(venueId: "not-a-venue-id"), WorldMetadataJson.Options)!;

        var venue = VenueResolver.Resolve(metadata);

        Assert.True(venue.Found);
        Assert.False(venue.FormatLooksValid);
        Assert.Contains(venue.Warnings, w => w.Contains("venue_"));
    }

    [Fact]
    public void Reports_missing_venue_id()
    {
        var metadata = System.Text.Json.JsonSerializer.Deserialize<WorldMetadata>(
            FixtureData.Json(venueId: null), WorldMetadataJson.Options)!;

        var venue = VenueResolver.Resolve(metadata);

        Assert.False(venue.Found);
        Assert.Null(venue.VenueId);
        Assert.NotEmpty(venue.Warnings);
    }
}
