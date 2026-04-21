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

    public async Task<PageSnapshot> CapturePageSnapshotAsync(Uri url, CancellationToken cancellationToken = default)
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

            var finalUrl = new Uri(page.Url);
            var title = await page.TitleAsync();
            title = string.IsNullOrEmpty(title) ? null : title;

            // Locator.AriaSnapshotAsync returns a compact YAML-like rendering of the
            // accessibility tree (roles + accessible names + values), which is what
            // Playwright's own assertion helpers use. Replaces the deprecated
            // IPage.Accessibility.SnapshotAsync() API.
            var ariaSnapshot = await page.Locator("body").AriaSnapshotAsync();
            if (!string.IsNullOrWhiteSpace(ariaSnapshot))
                return new PageSnapshot(finalUrl, title, ariaSnapshot, PageSnapshotSource.Accessibility);

            var body = await page.Locator("body").InnerTextAsync();
            return new PageSnapshot(finalUrl, title, body ?? string.Empty, PageSnapshotSource.InnerText);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    public async Task<IBrowserPage> OpenPageAsync(Uri url, CancellationToken cancellationToken = default)
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

            return new PlaywrightBrowserPage(page);
        }
        catch
        {
            await page.CloseAsync();
            throw;
        }
    }

    public ValueTask DisposeAsync() => new(context.CloseAsync());

}

internal sealed class PlaywrightBrowserPage(IPage page) : IBrowserPage
{
    public async Task NavigateAsync(Uri url, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var response = await page.GotoAsync(url.ToString(), new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded
        });
        if (response is null || !response.Ok)
            throw new InvalidOperationException(
                $"Navigation to {url} returned status {response?.Status.ToString() ?? "no response"}.");
    }

    public Task FillAsync(string selector, string value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return page.FillAsync(selector, value);
    }

    public Task ClickAsync(string selector, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return page.ClickAsync(selector);
    }

    public async Task WaitForSelectorAsync(
        string selector,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await page.WaitForSelectorAsync(selector, new PageWaitForSelectorOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = timeout is null ? null : (float)timeout.Value.TotalMilliseconds
            });
        }
        catch (TimeoutException)
        {
            throw;
        }
    }

    public Task<Uri> GetUrlAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new Uri(page.Url));
    }

    public async Task<string?> GetTextAsync(string selector, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var locator = page.Locator(selector);
        if (await locator.CountAsync() == 0)
            return null;
        return await locator.First.InnerTextAsync();
    }

    public ValueTask DisposeAsync() => new(page.CloseAsync());
}
