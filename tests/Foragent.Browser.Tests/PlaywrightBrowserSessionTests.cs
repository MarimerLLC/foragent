using Xunit;

namespace Foragent.Browser.Tests;

[Collection("Playwright")]
public class PlaywrightBrowserSessionTests(TestPageServerFixture fixture)
{
    [Fact]
    public async Task FetchPageTitleAsync_ReturnsTitle_FromPlainHtml()
    {
        await using var session = await fixture.Factory.CreateSessionAsync();

        var title = await session.FetchPageTitleAsync(fixture.Url("/plain"));

        Assert.Equal("Hello World", title);
    }

    [Fact]
    public async Task FetchPageTitleAsync_ReturnsNull_WhenTitleMissing()
    {
        await using var session = await fixture.Factory.CreateSessionAsync();

        var title = await session.FetchPageTitleAsync(fixture.Url("/no-title"));

        Assert.Null(title);
    }

    [Fact]
    public async Task FetchPageTitleAsync_DecodesHtmlEntities()
    {
        await using var session = await fixture.Factory.CreateSessionAsync();

        var title = await session.FetchPageTitleAsync(fixture.Url("/entities"));

        Assert.Equal("AT&T \u2014 Home", title);
    }

    [Fact]
    public async Task FetchPageTitleAsync_ReturnsJsUpdatedTitle()
    {
        await using var session = await fixture.Factory.CreateSessionAsync();

        var title = await session.FetchPageTitleAsync(fixture.Url("/js-title"));

        Assert.Equal("updated by js", title);
    }

    [Fact]
    public async Task FetchPageTitleAsync_FollowsRedirects()
    {
        await using var session = await fixture.Factory.CreateSessionAsync();

        var title = await session.FetchPageTitleAsync(fixture.Url("/redirect"));

        Assert.Equal("Hello World", title);
    }

    [Fact]
    public async Task FetchPageTitleAsync_ThrowsOn404()
    {
        await using var session = await fixture.Factory.CreateSessionAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.FetchPageTitleAsync(fixture.Url("/not-found")));
    }

    [Fact]
    public async Task Sessions_AreIndependent()
    {
        // Each session should get a fresh context per spec §3.5. A smoke check that
        // the factory doesn't hand back the same session twice or leak state.
        await using var sessionA = await fixture.Factory.CreateSessionAsync();
        await using var sessionB = await fixture.Factory.CreateSessionAsync();

        Assert.NotSame(sessionA, sessionB);

        var titleA = await sessionA.FetchPageTitleAsync(fixture.Url("/plain"));
        var titleB = await sessionB.FetchPageTitleAsync(fixture.Url("/entities"));

        Assert.Equal("Hello World", titleA);
        Assert.Equal("AT&T \u2014 Home", titleB);
    }
}
