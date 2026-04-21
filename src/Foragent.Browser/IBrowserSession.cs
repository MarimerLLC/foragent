namespace Foragent.Browser;

/// <summary>
/// Isolated browser context for a single A2A task. Disposal closes the
/// underlying browser context (cookies, storage, cache). The surrounding
/// <see cref="IBrowser"/> instance is long-lived and shared across sessions;
/// see spec §3.5.
/// </summary>
public interface IBrowserSession : IAsyncDisposable
{
    /// <summary>
    /// Navigates to <paramref name="url"/> and returns the contents of the
    /// rendered <c>&lt;title&gt;</c> element, or <c>null</c> if the page
    /// does not expose one.
    /// </summary>
    Task<string?> FetchPageTitleAsync(Uri url, CancellationToken cancellationToken = default);
}

/// <summary>
/// Creates fresh <see cref="IBrowserSession"/> instances against the shared
/// long-lived browser. Each session wraps a new browser context; disposing
/// the session disposes the context.
/// </summary>
public interface IBrowserSessionFactory
{
    Task<IBrowserSession> CreateSessionAsync(CancellationToken cancellationToken = default);
}
