using Xunit;

namespace Foragent.Browser.Tests;

[Collection("Playwright")]
public class PageSnapshotTests(TestPageServerFixture fixture)
{
    [Fact]
    public async Task CapturePageSnapshotAsync_ReturnsAccessibilityRendering_ForPlainHtml()
    {
        await using var session = await fixture.Factory.CreateSessionAsync();

        var snapshot = await session.CapturePageSnapshotAsync(fixture.Url("/plain"));

        Assert.Equal("Hello World", snapshot.Title);
        Assert.Equal(PageSnapshotSource.Accessibility, snapshot.Source);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Content));
        // The body text "hi" should appear somewhere in the accessibility tree.
        Assert.Contains("hi", snapshot.Content);
    }

    [Fact]
    public async Task CapturePageSnapshotAsync_FollowsRedirect_AndReportsFinalUrl()
    {
        await using var session = await fixture.Factory.CreateSessionAsync();

        var snapshot = await session.CapturePageSnapshotAsync(fixture.Url("/redirect"));

        Assert.EndsWith("/plain", snapshot.Url.AbsolutePath);
        Assert.Equal("Hello World", snapshot.Title);
    }

    [Fact]
    public async Task CapturePageSnapshotAsync_ThrowsOn404()
    {
        await using var session = await fixture.Factory.CreateSessionAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.CapturePageSnapshotAsync(fixture.Url("/not-found")));
    }
}
