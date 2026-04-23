using Microsoft.Playwright;

namespace Foragent.Browser;

internal sealed class PlaywrightBrowserSessionFactory(
    PlaywrightBrowserHost host) : IBrowserSessionFactory
{
    public Task<IBrowserSession> CreateSessionAsync(
        CancellationToken cancellationToken = default) =>
        CreateSessionAsync(static _ => true, cancellationToken);

    public async Task<IBrowserSession> CreateSessionAsync(
        Func<Uri, bool> allowedHost,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(allowedHost);
        var context = await host.Browser.NewContextAsync();

        // Install a context-wide route handler that aborts off-list navigations
        // and subframe loads before Playwright sees them (spec §7.1). This
        // intercepts Navigation requests (document/subframe); resource loads
        // (images, styles) pass through so pages can still render.
        await context.RouteAsync("**/*", async route =>
        {
            var request = route.Request;
            var resourceType = request.ResourceType;
            if (resourceType is not ("document" or "subframe"))
            {
                await route.ContinueAsync();
                return;
            }

            if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var target) ||
                !allowedHost(target))
            {
                await route.AbortAsync("accessdenied");
                return;
            }

            await route.ContinueAsync();
        });

        return new PlaywrightBrowserSession(context, allowedHost);
    }
}

internal sealed class PlaywrightBrowserSession(
    IBrowserContext context,
    Func<Uri, bool> allowedHost) : IBrowserSession
{
    public async Task<string?> FetchPageTitleAsync(Uri url, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAllowed(url);
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
        EnsureAllowed(url);
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
        EnsureAllowed(url);
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

    public async Task<IBrowserAgentPage> OpenAgentPageAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var page = await context.NewPageAsync();
        return new PlaywrightBrowserAgentPage(page, allowedHost);
    }

    public ValueTask DisposeAsync() => new(context.CloseAsync());

    private void EnsureAllowed(Uri url)
    {
        if (!allowedHost(url))
            throw new InvalidOperationException(
                $"Host '{url.Host}' is not in the session's allowlist.");
    }
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

internal sealed class PlaywrightBrowserAgentPage(
    IPage page,
    Func<Uri, bool> allowedHost) : IBrowserAgentPage
{
    public Uri CurrentUrl => Uri.TryCreate(page.Url, UriKind.Absolute, out var u) ? u : new Uri("about:blank");

    public async Task<string?> GetTitleAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var title = await page.TitleAsync();
        return string.IsNullOrEmpty(title) ? null : title;
    }

    public async Task NavigateAsync(Uri url, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!allowedHost(url))
            throw new InvalidOperationException(
                $"Host '{url.Host}' is not in the session's allowlist.");
        var response = await page.GotoAsync(url.ToString(), new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded
        });
        if (response is null || !response.Ok)
            throw new InvalidOperationException(
                $"Navigation to {url} returned status {response?.Status.ToString() ?? "no response"}.");
    }

    public async Task<string> AriaSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Ref-annotated aria snapshot — Playwright's "AI" mode emits [ref=eN]
        // identifiers that resolve via the aria-ref=eN locator dialect
        // (spec §9.1 step 6). In the 1.59 C# bindings this is gated behind
        // AriaSnapshotMode.Ai rather than a boolean Ref option.
        var snapshot = await page.Locator("body").AriaSnapshotAsync(
            new LocatorAriaSnapshotOptions { Mode = AriaSnapshotMode.Ai });
        return snapshot ?? string.Empty;
    }

    public Task ClickByRefAsync(string elementRef, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return page.Locator($"aria-ref={elementRef}").ClickAsync();
    }

    public Task TypeByRefAsync(string elementRef, string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return page.Locator($"aria-ref={elementRef}").FillAsync(text);
    }

    public async Task WaitForRefAsync(
        string elementRef,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await page.Locator($"aria-ref={elementRef}").WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = timeout is null ? null : (float)timeout.Value.TotalMilliseconds
        });
    }

    public ValueTask DisposeAsync() => new(page.CloseAsync());
}
