using System.Text.Json;
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

    public async Task SelectOptionAsync(string selector, string value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await page.Locator(selector).SelectOptionAsync(value);
    }

    public Task SetCheckedAsync(string selector, bool checked_, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return page.Locator(selector).SetCheckedAsync(checked_);
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

    public async Task<FormScan?> ScanFormAsync(
        string? formSelector = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // All the DOM walking happens inside the page — avoids N round-trips
        // to read each attribute. The JS returns a JSON-serializable shape
        // that mirrors FormScan/FormScanField.
        var raw = await page.EvaluateAsync<JsonElement?>(FormScanScript, formSelector);
        if (raw is null || raw.Value.ValueKind != JsonValueKind.Object)
            return null;

        var root = raw.Value;
        var url = new Uri(root.GetProperty("url").GetString()!);
        var selector = root.GetProperty("formSelector").GetString()!;
        var submit = root.TryGetProperty("submitSelector", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString()
            : null;

        var fields = new List<FormScanField>();
        foreach (var f in root.GetProperty("fields").EnumerateArray())
        {
            List<FormScanOption>? options = null;
            if (f.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Array)
            {
                options = [];
                foreach (var item in o.EnumerateArray())
                {
                    options.Add(new FormScanOption(
                        item.GetProperty("value").GetString() ?? string.Empty,
                        item.TryGetProperty("label", out var lbl) && lbl.ValueKind == JsonValueKind.String
                            ? lbl.GetString()
                            : null));
                }
            }

            fields.Add(new FormScanField(
                Tag: f.GetProperty("tag").GetString()!,
                InputType: StringOrNull(f, "type"),
                Name: StringOrNull(f, "name"),
                Id: StringOrNull(f, "id"),
                Label: StringOrNull(f, "label"),
                Required: f.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.True,
                Pattern: StringOrNull(f, "pattern"),
                Min: StringOrNull(f, "min"),
                Max: StringOrNull(f, "max"),
                MaxLength: f.TryGetProperty("maxLength", out var ml) && ml.ValueKind == JsonValueKind.Number ? ml.GetInt32() : null,
                Options: options,
                Selector: StringOrNull(f, "selector")));
        }

        return new FormScan(url, selector, submit, fields);
    }

    private static string? StringOrNull(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    // JS walker: resolves labels, collapses radio groups by name, emits a
    // canonical selector per field. Kept in one place so it's easy to update
    // when we discover a form shape the scan misses.
    private const string FormScanScript = """
        (formSelectorOrNull) => {
          const q = (root, sel) => root.querySelector(sel);
          const form = formSelectorOrNull ? q(document, formSelectorOrNull) : q(document, 'form');
          if (!form) return null;
          const labelFor = (el) => {
            if (el.id) {
              const lab = document.querySelector(`label[for="${CSS.escape(el.id)}"]`);
              if (lab) return lab.innerText.trim();
            }
            const wrap = el.closest('label');
            if (wrap) return wrap.innerText.trim();
            const aria = el.getAttribute('aria-label');
            if (aria) return aria.trim();
            const placeholder = el.getAttribute('placeholder');
            if (placeholder) return placeholder.trim();
            return null;
          };
          const emitSelector = (el) => {
            const tag = el.tagName.toLowerCase();
            const type = (el.type || '').toLowerCase();
            if (tag === 'input' && type === 'radio') {
              return el.name ? `input[type="radio"][name=${JSON.stringify(el.name)}]` : null;
            }
            if (el.name) return `${tag}[name=${JSON.stringify(el.name)}]`;
            if (el.id) return `#${CSS.escape(el.id)}`;
            return null;
          };
          const seenRadios = new Set();
          const fields = [];
          const controls = form.querySelectorAll('input, select, textarea');
          for (const el of controls) {
            const tag = el.tagName.toLowerCase();
            const type = (el.type || '').toLowerCase();
            if (tag === 'input' && (type === 'submit' || type === 'button' || type === 'reset' || type === 'image' || type === 'file')) continue;
            let options = null;
            if (tag === 'select') {
              options = Array.from(el.options).map(o => ({ value: o.value, label: (o.text || '').trim() }));
            } else if (tag === 'input' && type === 'radio') {
              if (!el.name || seenRadios.has(el.name)) continue;
              seenRadios.add(el.name);
              const group = form.querySelectorAll(`input[type="radio"][name="${CSS.escape(el.name)}"]`);
              options = Array.from(group).map(r => ({ value: r.value, label: labelFor(r) || r.value }));
            }
            const ml = el.maxLength;
            fields.push({
              tag,
              type: type || null,
              name: el.name || null,
              id: el.id || null,
              label: labelFor(el),
              required: !!el.required,
              pattern: el.getAttribute('pattern'),
              min: el.getAttribute('min'),
              max: el.getAttribute('max'),
              maxLength: (typeof ml === 'number' && ml > 0) ? ml : null,
              options,
              selector: emitSelector(el),
            });
          }
          let submitSelector = null;
          const submit = form.querySelector('button[type="submit"], input[type="submit"], button:not([type])');
          if (submit) {
            if (submit.id) submitSelector = `#${CSS.escape(submit.id)}`;
            else if (submit.getAttribute('name')) submitSelector = `${submit.tagName.toLowerCase()}[name=${JSON.stringify(submit.getAttribute('name'))}]`;
            else if (submit.tagName.toLowerCase() === 'button') submitSelector = 'button[type="submit"]';
            else submitSelector = 'input[type="submit"]';
          }
          let formSelector = formSelectorOrNull;
          if (!formSelector) {
            if (form.id) formSelector = `#${CSS.escape(form.id)}`;
            else if (form.getAttribute('name')) formSelector = `form[name=${JSON.stringify(form.getAttribute('name'))}]`;
            else formSelector = 'form';
          }
          return { url: location.href, formSelector, submitSelector, fields };
        }
        """;

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
