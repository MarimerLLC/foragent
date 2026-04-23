using Foragent.Browser;

namespace Foragent.Agent.Tests.BrowserTask;

internal sealed class FakeBrowserAgentPage : IBrowserAgentPage
{
    public List<string> Actions { get; } = [];
    public Uri CurrentUrl { get; set; } = new("about:blank");
    public string Snapshot { get; set; } = "- heading: stub page\n- button \"ok\" [ref=e1]";
    public string? Title { get; set; } = "stub";

    public Task<string?> GetTitleAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Title);

    public Task NavigateAsync(Uri url, CancellationToken cancellationToken = default)
    {
        Actions.Add($"navigate:{url}");
        CurrentUrl = url;
        return Task.CompletedTask;
    }

    public Task<string> AriaSnapshotAsync(CancellationToken cancellationToken = default)
    {
        Actions.Add("snapshot");
        return Task.FromResult(Snapshot);
    }

    public Task ClickByRefAsync(string elementRef, CancellationToken cancellationToken = default)
    {
        Actions.Add($"click:{elementRef}");
        return Task.CompletedTask;
    }

    public Task TypeByRefAsync(string elementRef, string text, CancellationToken cancellationToken = default)
    {
        Actions.Add($"type:{elementRef}:{text.Length}");
        return Task.CompletedTask;
    }

    public Task WaitForRefAsync(string elementRef, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        Actions.Add($"wait:{elementRef}");
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Actions.Add("dispose");
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeAgentBrowserSession(FakeBrowserAgentPage page) : IBrowserSession
{
    public Task<string?> FetchPageTitleAsync(Uri url, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<PageSnapshot> CapturePageSnapshotAsync(Uri url, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<IBrowserPage> OpenPageAsync(Uri url, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<IBrowserAgentPage> OpenAgentPageAsync(CancellationToken ct = default) =>
        Task.FromResult<IBrowserAgentPage>(page);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeAgentBrowserSessionFactory(FakeBrowserAgentPage page) : IBrowserSessionFactory
{
    public Func<Uri, bool>? CapturedAllowlist { get; private set; }
    public int SessionsCreated { get; private set; }

    public Task<IBrowserSession> CreateSessionAsync(CancellationToken ct = default)
    {
        SessionsCreated++;
        return Task.FromResult<IBrowserSession>(new FakeAgentBrowserSession(page));
    }

    public Task<IBrowserSession> CreateSessionAsync(Func<Uri, bool> allowedHost, CancellationToken ct = default)
    {
        CapturedAllowlist = allowedHost;
        SessionsCreated++;
        return Task.FromResult<IBrowserSession>(new FakeAgentBrowserSession(page));
    }
}
