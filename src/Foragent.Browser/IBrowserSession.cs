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

    /// <summary>
    /// Opens a page for a multi-step flow (login, fill form, navigate, read
    /// back confirmation). The caller drives the page with the methods on
    /// <see cref="IBrowserPage"/> and disposes it when done. The surrounding
    /// session's context still owns cookies / storage — close the page when
    /// finished, dispose the session when the task ends.
    /// </summary>
    Task<IBrowserPage> OpenPageAsync(Uri url, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a page suited to an LLM-in-the-loop planner: exposes
    /// ref-annotated aria snapshots (<see cref="IBrowserAgentPage.AriaSnapshotAsync"/>)
    /// and ref-based interactions resolved via Playwright's <c>aria-ref=eN</c>
    /// locator dialect. No initial URL is required; the planner drives
    /// navigation through its own tool calls. Used by the
    /// <c>browser-task</c> generalist (spec §5.2).
    /// </summary>
    Task<IBrowserAgentPage> OpenAgentPageAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A stateful page inside an <see cref="IBrowserSession"/>. The grain is low
/// enough to drive arbitrary HTML forms but the methods stay Playwright-free
/// so capabilities don't pick up a hard dependency on Microsoft.Playwright.
/// Selectors follow Playwright's syntax — CSS, text=, role=, etc. Sensitive
/// values passed to <see cref="FillAsync"/> must not be logged by the
/// implementation.
/// </summary>
public interface IBrowserPage : IAsyncDisposable
{
    /// <summary>Navigates the page to a new URL.</summary>
    Task NavigateAsync(Uri url, CancellationToken cancellationToken = default);

    /// <summary>Fills a field matched by <paramref name="selector"/>.</summary>
    Task FillAsync(string selector, string value, CancellationToken cancellationToken = default);

    /// <summary>Clicks the element matched by <paramref name="selector"/>.</summary>
    Task ClickAsync(string selector, CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits until the element matched by <paramref name="selector"/> is attached
    /// and visible. Throws <see cref="TimeoutException"/> on timeout.
    /// </summary>
    Task WaitForSelectorAsync(
        string selector,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    /// <summary>The current URL, after any redirects and client-side navigations.</summary>
    Task<Uri> GetUrlAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the inner text of the element matched by <paramref name="selector"/>,
    /// or <c>null</c> if no element matches. Useful for reading back error
    /// messages or confirmation text.
    /// </summary>
    Task<string?> GetTextAsync(string selector, CancellationToken cancellationToken = default);
}

/// <summary>
/// Ref-based page surface for LLM-in-the-loop planners. Each call to
/// <see cref="AriaSnapshotAsync"/> returns a tree annotated with
/// <c>[ref=eN]</c> ids; <see cref="ClickByRefAsync"/>, <see cref="TypeByRefAsync"/>,
/// and <see cref="WaitForRefAsync"/> resolve those refs via Playwright's
/// <c>aria-ref=eN</c> locator dialect. Refs are valid only within the
/// snapshot they came from — the planner must re-snapshot after any
/// mutation (spec §9.1 step 6, decision D1 — no cache).
/// </summary>
public interface IBrowserAgentPage : IAsyncDisposable
{
    /// <summary>The current URL, after any redirects and client-side navigations.</summary>
    Uri CurrentUrl { get; }

    /// <summary>The current page title, or <c>null</c> if absent.</summary>
    Task<string?> GetTitleAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Navigates to <paramref name="url"/>. The implementation must respect
    /// the session's allowlist — off-list navigations fail with
    /// <see cref="InvalidOperationException"/> before Playwright issues the request.
    /// </summary>
    Task NavigateAsync(Uri url, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a ref-annotated aria snapshot of the current page. Each
    /// interactive element carries <c>[ref=eN]</c>; planners pass the ref
    /// back to <see cref="ClickByRefAsync"/>/<see cref="TypeByRefAsync"/>.
    /// </summary>
    Task<string> AriaSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>Clicks the element identified by <paramref name="elementRef"/> (e.g. <c>e12</c>).</summary>
    Task ClickByRefAsync(string elementRef, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fills the element identified by <paramref name="elementRef"/>. Used for
    /// input/textarea/contenteditable. Sensitive values must not be logged.
    /// </summary>
    Task TypeByRefAsync(string elementRef, string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits until the element identified by <paramref name="elementRef"/> is
    /// visible. Throws <see cref="TimeoutException"/> on timeout.
    /// </summary>
    Task WaitForRefAsync(
        string elementRef,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
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

    /// <summary>
    /// Creates a session whose navigations and subframe loads are restricted to
    /// hosts accepted by <paramref name="allowedHost"/>. An off-list request is
    /// aborted inside the browser context before Playwright issues it
    /// (spec §7.1). Passing a predicate that always returns <c>false</c>
    /// effectively rejects all navigation.
    /// </summary>
    Task<IBrowserSession> CreateSessionAsync(
        Func<Uri, bool> allowedHost,
        CancellationToken cancellationToken = default);
}
