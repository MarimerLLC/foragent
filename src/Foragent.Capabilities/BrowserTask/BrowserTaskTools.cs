using System.ComponentModel;
using Foragent.Browser;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Foragent.Capabilities.BrowserTask;

/// <summary>
/// The tool surface exposed to the planner LLM for a single browser-task
/// run. Methods are wrapped into <see cref="AIFunction"/> instances via
/// <see cref="AIFunctionFactory.Create(Delegate, AIFunctionFactoryOptions)"/>.
/// The <see cref="FunctionInvokingChatClient"/> that wraps Foragent's
/// <see cref="IChatClient"/> (see <c>AddRockBotTieredChatClients</c>)
/// invokes these between model turns; no separate planner loop is needed.
/// Instances are per-task — they close over one <see cref="IBrowserAgentPage"/>
/// and one <see cref="BrowserTaskState"/>.
/// </summary>
internal sealed class BrowserTaskTools
{
    private readonly IBrowserAgentPage _page;
    private readonly BrowserTaskState _state;
    private readonly HostAllowlist _allowlist;
    private readonly ILogger _logger;

    public BrowserTaskTools(
        IBrowserAgentPage page,
        BrowserTaskState state,
        HostAllowlist allowlist,
        ILogger logger)
    {
        _page = page;
        _state = state;
        _allowlist = allowlist;
        _logger = logger;
    }

    public AIFunction[] BuildFunctions() =>
    [
        AIFunctionFactory.Create(
            Snapshot,
            name: "snapshot",
            description: "Capture a ref-annotated accessibility snapshot of the current page. Returns a YAML-ish aria tree where each interactive element carries [ref=eN]; pass those refs to click/type/wait_for. Re-snapshot after every click/type/navigate — refs from a previous snapshot are invalid once the page mutates."),
        AIFunctionFactory.Create(
            Navigate,
            name: "navigate",
            description: "Navigate the current page to an absolute URL. The URL's host must be on the task's allowlist; off-list navigation is rejected before the request is issued."),
        AIFunctionFactory.Create(
            Click,
            name: "click",
            description: "Click an element by ref (e.g. 'e12'). Refs come from the most recent snapshot."),
        AIFunctionFactory.Create(
            Type,
            name: "type",
            description: "Fill an input/textarea/contenteditable identified by ref. Pass the target value; prior contents are replaced."),
        AIFunctionFactory.Create(
            WaitFor,
            name: "wait_for",
            description: "Wait until the element identified by ref is visible. Use after an action that triggers navigation or async UI update."),
        AIFunctionFactory.Create(
            Done,
            name: "done",
            description: "Mark the task complete. Pass a short summary of what was accomplished and optionally a structured result string. After calling done, stop emitting tool calls."),
        AIFunctionFactory.Create(
            Fail,
            name: "fail",
            description: "Mark the task failed with a reason explaining what went wrong. After calling fail, stop emitting tool calls.")
    ];

    private const string BudgetMessage = "Step budget exhausted — call done() with whatever was achieved, or fail() with a reason. Do not call other tools.";

    [Description("Capture a ref-annotated aria snapshot of the current page.")]
    private async Task<string> Snapshot()
    {
        if (_state.BudgetExhausted) return BudgetMessage;
        _state.IncrementStep();
        var url = _page.CurrentUrl;
        var title = await _page.GetTitleAsync();
        var snapshot = await _page.AriaSnapshotAsync();
        _state.RecordNavigation(url);
        _logger.LogInformation("browser-task step {Step}: snapshot {Url}", _state.Steps, url);
        return $"Url: {url}\nTitle: {title ?? "(none)"}\n\n{snapshot}";
    }

    [Description("Navigate to an absolute URL within the allowlist.")]
    private async Task<string> Navigate(
        [Description("Absolute http(s) URL to load. Must match an allowlist pattern.")] string url)
    {
        if (_state.BudgetExhausted) return BudgetMessage;
        _state.IncrementStep();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target) ||
            (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
            return $"Rejected: '{url}' is not an absolute http(s) URL.";
        if (!_allowlist.IsAllowed(target))
            return $"Rejected: host '{target.Host}' is not on the allowlist.";
        await _page.NavigateAsync(target);
        _state.RecordNavigation(target);
        _logger.LogInformation("browser-task step {Step}: navigate {Url}", _state.Steps, target);
        return $"Loaded {target}. Call snapshot to see the page.";
    }

    [Description("Click an element by ref.")]
    private async Task<string> Click(
        [Description("The element ref (e.g. 'e12') from the latest snapshot.")] string @ref)
    {
        if (_state.BudgetExhausted) return BudgetMessage;
        _state.IncrementStep();
        await _page.ClickByRefAsync(@ref);
        _logger.LogInformation("browser-task step {Step}: click ref={Ref}", _state.Steps, @ref);
        return $"Clicked {@ref}. Call snapshot to see the resulting page.";
    }

    [Description("Fill a field by ref.")]
    private async Task<string> Type(
        [Description("The element ref (e.g. 'e12') from the latest snapshot.")] string @ref,
        [Description("Text to enter into the field, replacing any prior contents.")] string text)
    {
        if (_state.BudgetExhausted) return BudgetMessage;
        _state.IncrementStep();
        await _page.TypeByRefAsync(@ref, text);
        // Never log the value — may be a password or other sensitive content.
        _logger.LogInformation("browser-task step {Step}: type ref={Ref} ({Length} chars)",
            _state.Steps, @ref, text.Length);
        return $"Typed into {@ref}.";
    }

    [Description("Wait for an element to become visible.")]
    private async Task<string> WaitFor(
        [Description("The element ref to wait for.")] string @ref,
        [Description("Timeout in seconds; default 10.")] int? timeoutSeconds = null)
    {
        if (_state.BudgetExhausted) return BudgetMessage;
        _state.IncrementStep();
        var timeout = TimeSpan.FromSeconds(timeoutSeconds ?? 10);
        try
        {
            await _page.WaitForRefAsync(@ref, timeout);
            return $"{@ref} visible.";
        }
        catch (TimeoutException)
        {
            return $"Timeout: {@ref} did not become visible within {timeout.TotalSeconds:0}s.";
        }
    }

    [Description("Mark the task complete.")]
    private string Done(
        [Description("One-sentence summary of what was accomplished.")] string summary,
        [Description("Optional structured result text (JSON, extracted value, etc.). Omit if no result is expected.")] string? result = null)
    {
        _state.Completed(summary, result);
        _logger.LogInformation("browser-task done after {Steps} step(s): {Summary}", _state.Steps, summary);
        return "Task marked complete. Stop emitting tool calls.";
    }

    [Description("Mark the task failed.")]
    private string Fail(
        [Description("Reason the task could not complete.")] string reason)
    {
        _state.Failed(reason);
        _logger.LogWarning("browser-task failed after {Steps} step(s): {Reason}", _state.Steps, reason);
        return "Task marked failed. Stop emitting tool calls.";
    }
}

/// <summary>
/// Shared state between the tool surface and the capability wrapper. Not
/// thread-safe — a browser-task runs one tool at a time inside the
/// function-invoking chat client.
/// </summary>
internal sealed class BrowserTaskState
{
    public int Steps { get; private set; }
    public int MaxSteps { get; set; } = int.MaxValue;
    public bool IsTerminal => IsDone || IsFailed;
    public bool IsDone { get; private set; }
    public bool IsFailed { get; private set; }
    public string? Summary { get; private set; }
    public string? Result { get; private set; }
    public string? FailureReason { get; private set; }
    public List<Uri> Navigations { get; } = [];

    public bool BudgetExhausted => Steps >= MaxSteps;

    public void IncrementStep() => Steps++;

    public void RecordNavigation(Uri url)
    {
        if (Navigations.Count == 0 || Navigations[^1] != url)
            Navigations.Add(url);
    }

    public void Completed(string summary, string? result)
    {
        IsDone = true;
        Summary = summary;
        Result = result;
    }

    public void Failed(string reason)
    {
        IsFailed = true;
        FailureReason = reason;
    }
}
