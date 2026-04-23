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
    /// Selects the option with <paramref name="value"/> in the <c>&lt;select&gt;</c>
    /// matched by <paramref name="selector"/>. Throws if the option is absent.
    /// </summary>
    Task SelectOptionAsync(string selector, string value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the checked state of a checkbox or radio input matched by
    /// <paramref name="selector"/>. Unlike <see cref="ClickAsync"/>, this is
    /// idempotent — calling with <c>true</c> when the box is already checked
    /// is a no-op rather than a toggle.
    /// </summary>
    Task SetCheckedAsync(string selector, bool checked_, CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Scans the first <c>&lt;form&gt;</c> matching <paramref name="formSelector"/>
    /// (or the first form on the page when <paramref name="formSelector"/> is
    /// <c>null</c>) and returns a structured description of its inputs, selects,
    /// textareas, labels, validation attributes, and submit button. Produces no
    /// LLM output — purely deterministic DOM reading — so callers can use it as
    /// the skeleton for a typed <c>FormSchema</c>. Returns <c>null</c> when no
    /// form is found. Radio groups are collapsed to a single field per name
    /// with all options enumerated.
    /// </summary>
    Task<FormScan?> ScanFormAsync(string? formSelector = null, CancellationToken cancellationToken = default);
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
/// A deterministic rendering of an HTML form. Produced by
/// <see cref="IBrowserPage.ScanFormAsync"/>; the <c>learn-form-schema</c>
/// capability lifts this into the wire-level <c>FormSchema</c> with optional
/// LLM enrichment (dropdown dependencies, validation hints).
/// </summary>
/// <param name="Url">The URL the scan was taken on (after redirects).</param>
/// <param name="FormSelector">A CSS selector that reaches the scanned form — either the one the caller passed in, or a generated one based on the form's id/name.</param>
/// <param name="SubmitSelector">Selector for the form's submit control, or <c>null</c> if none was detected.</param>
/// <param name="Fields">Fields detected in document order. Radio groups appear once per group name.</param>
public sealed record FormScan(
    Uri Url,
    string FormSelector,
    string? SubmitSelector,
    IReadOnlyList<FormScanField> Fields);

/// <summary>
/// One field detected by <see cref="IBrowserPage.ScanFormAsync"/>. Carries raw
/// HTML attributes — the capability layer decides how to map <see cref="Tag"/>
/// + <see cref="InputType"/> to its typed <c>FormFieldType</c>.
/// </summary>
/// <param name="Tag">The element tag — <c>input</c>, <c>select</c>, or <c>textarea</c>.</param>
/// <param name="InputType">The <c>type</c> attribute for <c>&lt;input&gt;</c> elements (<c>text</c>, <c>email</c>, …); <c>null</c> for non-input tags.</param>
/// <param name="Name">The <c>name</c> attribute, or <c>null</c>.</param>
/// <param name="Id">The <c>id</c> attribute, or <c>null</c>.</param>
/// <param name="Label">Visible label text resolved via <c>label[for=id]</c>, a wrapping <c>&lt;label&gt;</c>, <c>aria-label</c>, or the placeholder.</param>
/// <param name="Required">Whether the element carries the <c>required</c> attribute.</param>
/// <param name="Pattern">The HTML5 <c>pattern</c> attribute, or <c>null</c>.</param>
/// <param name="Min">The HTML5 <c>min</c> attribute, or <c>null</c>.</param>
/// <param name="Max">The HTML5 <c>max</c> attribute, or <c>null</c>.</param>
/// <param name="MaxLength">The HTML5 <c>maxlength</c> attribute, or <c>null</c> when unspecified or non-positive.</param>
/// <param name="Options">Enumerated options for <c>select</c> and radio groups; <c>null</c> for free-text fields.</param>
/// <param name="Selector">A CSS selector the capability can use to drive the field; <c>null</c> when neither name nor id is present.</param>
public sealed record FormScanField(
    string Tag,
    string? InputType,
    string? Name,
    string? Id,
    string? Label,
    bool Required,
    string? Pattern,
    string? Min,
    string? Max,
    int? MaxLength,
    IReadOnlyList<FormScanOption>? Options,
    string? Selector);

/// <summary>An option entry for a <c>&lt;select&gt;</c> or radio group.</summary>
public sealed record FormScanOption(string Value, string? Label);

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
