using Microsoft.Playwright;

namespace Foragent.Browser;

internal sealed class PlaywrightBrowserSessionFactory(
    PlaywrightBrowserHost host) : IBrowserSessionFactory
{
    public async Task<IBrowserSession> CreateSessionAsync(
        CancellationToken cancellationToken = default)
    {
        var context = await host.Browser.NewContextAsync();
        return new PlaywrightBrowserSession(context);
    }
}

internal sealed class PlaywrightBrowserSession(IBrowserContext context) : IBrowserSession
{
    public async Task<string?> FetchPageTitleAsync(Uri url, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var page = await context.NewPageAsync();
        try
        {
            var response = await page.GotoAsync(url.ToString(), new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded
            });
            if (response is null || !response.Ok)
                throw new InvalidOperationException(
                    $"Navigation to {url} returned status {response?.Status.ToString() ?? "no response"}.");

            var title = await page.TitleAsync();
            return string.IsNullOrEmpty(title) ? null : title;
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    public ValueTask DisposeAsync() => new(context.CloseAsync());
}
