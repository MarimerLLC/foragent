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

    /// <summary>
    /// Navigates to <paramref name="url"/> and returns a compact,
    /// LLM-consumable representation of the page. Uses the Chromium
    /// accessibility tree when available (spec §3.2 — the right grain for an
    /// extraction prompt); falls back to <c>&lt;body&gt;</c> inner text when
    /// the accessibility snapshot is empty or unavailable. Includes the page
    /// title at the top so the LLM has enough context to reason.
    /// </summary>
    Task<PageSnapshot> CapturePageSnapshotAsync(Uri url, CancellationToken cancellationToken = default);
}

/// <summary>
/// A compact rendering of a page suitable for LLM prompting.
/// </summary>
/// <param name="Url">The final URL after any redirects.</param>
/// <param name="Title">Page title, or <c>null</c>.</param>
/// <param name="Content">Accessibility-tree rendering or inner text.</param>
/// <param name="Source">Whether the content came from the accessibility tree or from a text fallback.</param>
public sealed record PageSnapshot(Uri Url, string? Title, string Content, PageSnapshotSource Source);

public enum PageSnapshotSource
{
    Accessibility,
    InnerText
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
