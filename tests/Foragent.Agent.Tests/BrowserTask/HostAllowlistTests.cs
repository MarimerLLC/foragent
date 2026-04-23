using Foragent.Capabilities.BrowserTask;
using Xunit;

namespace Foragent.Agent.Tests.BrowserTask;

public class HostAllowlistTests
{
    [Fact]
    public void ExactHost_Matches_CaseInsensitively()
    {
        var list = HostAllowlist.Parse(["bsky.app"]);
        Assert.True(list.IsAllowed(new Uri("https://bsky.app/")));
        Assert.True(list.IsAllowed(new Uri("https://BSKY.APP/")));
        Assert.False(list.IsAllowed(new Uri("https://foo.bsky.app/")));
        Assert.False(list.IsAllowed(new Uri("https://example.com/")));
    }

    [Fact]
    public void SubdomainWildcard_MatchesSubdomainButNotBareDomain()
    {
        var list = HostAllowlist.Parse(["*.example.com"]);
        Assert.True(list.IsAllowed(new Uri("https://foo.example.com/")));
        Assert.True(list.IsAllowed(new Uri("https://a.b.example.com/")));
        Assert.False(list.IsAllowed(new Uri("https://example.com/")));
        Assert.False(list.IsAllowed(new Uri("https://example.org/")));
    }

    [Fact]
    public void StarAloneMatchesAny()
    {
        var list = HostAllowlist.Parse(["*"]);
        Assert.True(list.IsAllowed(new Uri("https://any.host.tld/")));
    }

    [Fact]
    public void EmptyListIsRejected()
    {
        Assert.Throws<ArgumentException>(() => HostAllowlist.Parse([]));
        Assert.Throws<ArgumentException>(() => HostAllowlist.Parse([""]));
        Assert.Throws<ArgumentException>(() => HostAllowlist.Parse(["  "]));
    }

    [Fact]
    public void MultiPatternList_OrsEntries()
    {
        var list = HostAllowlist.Parse(["bsky.app", "*.example.com"]);
        Assert.True(list.IsAllowed(new Uri("https://bsky.app/")));
        Assert.True(list.IsAllowed(new Uri("https://foo.example.com/")));
        Assert.False(list.IsAllowed(new Uri("https://example.com/")));
    }

    [Theory]
    [InlineData("*.")]
    [InlineData("*.*")]
    [InlineData("foo*bar")]
    public void InvalidPattern_IsRejected(string pattern)
    {
        Assert.Throws<ArgumentException>(() => HostAllowlist.Parse([pattern]));
    }

    [Fact]
    public void PreservesPatterns_ForAuditLogging()
    {
        var list = HostAllowlist.Parse([" bsky.app ", "*.Example.com"]);
        Assert.Equal(["bsky.app", "*.example.com"], list.Patterns);
    }
}
