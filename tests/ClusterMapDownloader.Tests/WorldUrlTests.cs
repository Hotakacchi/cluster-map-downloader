using ClusterMapDownloader;
using Xunit;

namespace ClusterMapDownloader.Tests;

public class WorldUrlTests
{
    [Fact]
    public void Parses_canonical_https_url()
    {
        var url = WorldUrl.Parse("https://cluster.mu/w/fa1afe1a00004000800000000000abcd");
        Assert.Equal("fa1afe1a00004000800000000000abcd", url.WorldId);
        Assert.Equal("https://cluster.mu/w/fa1afe1a00004000800000000000abcd", url.CanonicalUrl);
        Assert.False(url.IsGuidLike);
    }

    [Fact]
    public void Parses_guid_like_world_id_from_url()
    {
        var id = FixtureData.SyntheticWorldId; // 11111111-2222-3333-4444-555555555555
        var url = WorldUrl.Parse($"{FixtureData.SyntheticWorldUrl}/buy?x=1");
        Assert.Equal(id, url.WorldId);
        Assert.True(url.IsGuidLike);
    }

    [Fact]
    public void Parses_http_www_and_locale_variants()
    {
        var id = FixtureData.SyntheticWorldId;
        foreach (var input in new[]
                 {
                     $"http://cluster.mu/w/{id}",
                     $"http://www.cluster.mu/w/{id}",
                     $"https://www.cluster.mu/w/{id}",
                     $"https://cluster.mu/en/w/{id}",
                     $"https://cluster.mu/w/{id}/",
                     $"https://cluster.mu/w/{id}?_=1",
                     $"cluster.mu/w/{id}",
                     id,
                 })
        {
            var url = WorldUrl.Parse(input);
            Assert.Equal(id, url.WorldId);
        }
    }

    [Fact]
    public void Accepts_bare_world_id_without_slashes()
    {
        var url = WorldUrl.Parse(FixtureData.SyntheticWorldId);
        Assert.Equal(FixtureData.SyntheticWorldId, url.WorldId);
        Assert.True(url.IsGuidLike);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://example.com/w/deadbeefdeadbeefdeadbeef")]
    [InlineData("https://cluster.mu/nope/deadbeefdeadbeefdeadbeef")]
    [InlineData("some/file/path")]
    [InlineData("https//cluster.mu/w/fa1afe1a00004000800000000000abcd")]
    public void Rejects_invalid_inputs(string input)
    {
        Assert.False(WorldUrl.TryParse(input, out var result, out var error));
        Assert.Null(result);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Throws<WorldUrlFormatException>(() => WorldUrl.Parse(input));
    }

    [Fact]
    public void Warns_on_non_guid_like_but_accepts()
    {
        Assert.True(WorldUrl.TryParse("worldname123", out var url, out _));
        Assert.NotNull(url);
        Assert.False(url!.IsGuidLike);
    }
}
