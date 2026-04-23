using System.Text;
using System.Text.Json;
using Foragent.Browser;
using Foragent.Credentials;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.A2A;
using RockBot.Host;

namespace Foragent.Capabilities.BrowserTask;

/// <summary>
/// The generalist <c>browser-task</c> capability (spec §5.2). Runs an
/// LLM-in-the-loop planner over a small ref-based tool surface against a
/// per-task <see cref="IBrowserAgentPage"/>. This is Foragent's primary
/// capability — specialists exist only where deterministic, programmatic
/// callers benefit from a typed shape.
///
/// Step 7 wires the learning substrate: <see cref="ISkillStore"/> +
/// <see cref="ILongTermMemory"/> provide priming on plan, and successful
/// tasks write a learned skill back so subsequent runs against the same
/// site benefit from accumulated knowledge (spec §5.6).
/// </summary>
public sealed class BrowserTaskCapability(
    IBrowserSessionFactory browserFactory,
    IChatClient chatClient,
    ICredentialBroker credentialBroker,
    BrowserTaskPriming priming,
    ISkillStore skillStore,
    ILogger<BrowserTaskCapability> logger) : ICapability
{
    public static AgentSkill SkillDefinition { get; } = new()
    {
        Id = "browser-task",
        Name = "Browser Task (generalist)",
        Description = "Drive a browser with an LLM-in-the-loop planner to accomplish a free-form intent. "
            + "Input: JSON {\"intent\":\"...\",\"allowedHosts\":[\"host\",\"*.host\",\"*\"],\"url\":\"optional start\",\"credentialId\":\"optional\",\"maxSteps\":60,\"maxSeconds\":120}. "
            + "Returns a short summary plus optional structured result string."
    };

    private const string SystemPrompt = """
        You drive a real web browser to accomplish a task on behalf of another agent.

        You have these tools:
          - snapshot() — returns a ref-annotated aria tree of the current page. ALWAYS call this first, and again after any click/type/navigate, because refs expire when the page mutates.
          - navigate(url) — load a URL. The URL's host must be on the task's allowlist.
          - click(ref) — click an element by its [ref=eN] id from the latest snapshot.
          - type(ref, text) — fill an input by ref with the given text.
          - wait_for(ref, timeoutSeconds?) — wait for an element to be visible.
          - done(summary, result?) — call exactly once when the task is complete. After calling done, return a short final message and DO NOT emit further tool calls.
          - fail(reason) — call if the task cannot be completed. After calling fail, stop emitting tool calls.

        Rules:
          - Stay on the allowlisted hosts the caller provided. Off-list navigations are rejected.
          - If an element you want is not in the latest snapshot, re-snapshot rather than guessing a ref.
          - Never enter credentials or secrets yourself — if the task needs authentication, call fail and explain.
          - Be efficient: each tool call counts toward a step budget.
          - When the task is done, call done() with a concise summary. If the caller asked for a specific value (e.g. a page title), include it as the result argument.
        """;

    public string SkillId => SkillDefinition.Id;
    public AgentSkill Skill => SkillDefinition;

    public async Task<AgentTaskResult> ExecuteAsync(AgentTaskRequest request, AgentTaskContext context)
    {
        var ct = context.MessageContext.CancellationToken;
        var input = BrowserTaskInput.Parse(request);

        if (input.Error is not null)
            return CapabilityResult.Error(request, input.Error);

        // credentialId is accepted by the input shape but not consumed by the
        // planner in step 6. Resolve it so we fail fast (and audit-log access)
        // if the caller references an unknown id. A future step exposes a
        // typed login tool that actually uses the resolved values.
        if (!string.IsNullOrWhiteSpace(input.CredentialId))
        {
            try
            {
                _ = await credentialBroker.ResolveAsync(input.CredentialId!, ct);
            }
            catch (CredentialNotFoundException ex)
            {
                return CapabilityResult.Error(request, $"Credential '{ex.CredentialId}' is not configured.");
            }
        }

        using var budgetCts = new CancellationTokenSource(TimeSpan.FromSeconds(input.MaxSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, budgetCts.Token);

        var state = new BrowserTaskState();

        try
        {
            await using var session = await browserFactory.CreateSessionAsync(input.Allowlist!.IsAllowed, linkedCts.Token);
            await using var page = await session.OpenAgentPageAsync(linkedCts.Token);

            if (input.Url is not null)
                await page.NavigateAsync(input.Url, linkedCts.Token);

            state.MaxSteps = input.MaxSteps;
            var tools = new BrowserTaskTools(page, state, input.Allowlist!, logger).BuildFunctions();

            var primingText = await priming.BuildAsync(input.Intent!, input.Allowlist!, linkedCts.Token);
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, SystemPrompt),
                new(ChatRole.User, BuildUserPrompt(input, primingText))
            };

            var options = new ChatOptions
            {
                Tools = [.. tools],
                ToolMode = ChatToolMode.Auto
                // Step budget is enforced tool-side (BrowserTaskState) and
                // wall-clock via linkedCts. The function-invoking chat client
                // does not currently expose a per-request iteration cap that
                // works through ChatOptions — noted in framework-feedback.
            };

            // The IChatClient we inject is the RockBot-wrapped
            // FunctionInvokingChatClient (see Program.cs tiered registration).
            // It runs the full model ↔ tool loop internally and returns the
            // final assistant response when the model stops emitting tool
            // calls or the iteration cap trips.
            try
            {
                _ = await chatClient.GetResponseAsync(messages, options, linkedCts.Token);
            }
            catch (OperationCanceledException) when (budgetCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                logger.LogInformation(
                    "browser-task budget exhausted after {Seconds}s / {Steps} step(s)",
                    input.MaxSeconds, state.Steps);
            }

            if (state.IsDone)
                await TryWriteLearnedSkillAsync(input, state, linkedCts.Token);

            return BuildResult(request, input, state);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "browser-task failed after {Steps} step(s)", state.Steps);
            return CapabilityResult.Error(request, $"Browser task failed: {ex.Message}");
        }
    }

    private static string BuildUserPrompt(BrowserTaskInput input, string? primingText)
    {
        var sb = new StringBuilder();
        sb.Append("Intent: ").AppendLine(input.Intent);
        if (input.Url is not null)
            sb.Append("Starting URL: ").AppendLine(input.Url.ToString());
        sb.Append("Allowed hosts: ").AppendLine(string.Join(", ", input.Allowlist!.Patterns));
        sb.Append("Step budget: ").Append(input.MaxSteps).Append(" steps / ").Append(input.MaxSeconds).AppendLine("s wall-clock.");
        if (!string.IsNullOrWhiteSpace(input.CredentialId))
            sb.AppendLine("A credential id was provided but is not yet exposed as a tool. If authentication is required, call fail().");
        if (!string.IsNullOrWhiteSpace(primingText))
        {
            sb.AppendLine();
            sb.Append(primingText);
        }
        return sb.ToString();
    }

    private async Task TryWriteLearnedSkillAsync(
        BrowserTaskInput input,
        BrowserTaskState state,
        CancellationToken ct)
    {
        // Only write when we have something substantive to remember. A task
        // that completed in one navigation carries no multi-step knowledge
        // worth priming future runs with.
        if (state.Navigations.Count < 2)
            return;

        var primaryHost = input.Allowlist!.Patterns
            .Select(p => p.TrimStart('*', '.'))
            .FirstOrDefault(p => !string.IsNullOrEmpty(p) && p != "*");
        if (string.IsNullOrEmpty(primaryHost))
            return;

        var hostSegment = SkillNaming.SanitizeHost(primaryHost);
        var slug = Slugify(input.Intent!);
        var name = $"sites/{hostSegment}/learned/{slug}";

        try
        {
            var existing = await skillStore.GetAsync(name);
            var synthesizerMessages = new List<ChatMessage>
            {
                new(ChatRole.System, LearnedSkillSystemPrompt),
                new(ChatRole.User, BuildLearnedSkillPrompt(input, state, existing))
            };
            // No tools on this turn — the model is just summarising.
            var response = await chatClient.GetResponseAsync(
                synthesizerMessages,
                new ChatOptions { Tools = [] },
                ct);
            var text = response.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
                return;

            var (summary, content) = SplitSummaryAndContent(text);
            var skill = new Skill(
                Name: name,
                Summary: summary,
                Content: content,
                CreatedAt: existing?.CreatedAt ?? DateTimeOffset.UtcNow,
                UpdatedAt: existing is null ? null : DateTimeOffset.UtcNow,
                LastUsedAt: DateTimeOffset.UtcNow,
                SeeAlso: [$"sites/{hostSegment}/login"]);
            await skillStore.SaveAsync(skill);
            logger.LogInformation("Wrote learned skill '{Name}' ({Nav} navigations, {Steps} steps).",
                name, state.Navigations.Count, state.Steps);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Budget was exhausted during synthesis; the task itself already
            // succeeded, so swallow and move on.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write learned skill '{Name}'.", name);
        }
    }

    private const string LearnedSkillSystemPrompt = """
        You are writing a reusable skill for a browser-automation agent after a task just succeeded.

        Output format:
          - Line 1: a single-sentence summary of 15 words or less.
          - Line 2: blank.
          - Remainder: markdown explaining the flow so a future planner can repeat it efficiently. Cover: landing URL, key steps, selectors or labels that actually worked, known pitfalls.

        Rules:
          - Do NOT include credential values, secrets, or any specific text the user typed into fields.
          - Do NOT fabricate details the trace does not show.
          - Prefer the phrases a planner would search for later (site name, feature name).
          - Keep it under ~400 words.
        """;

    private static string BuildLearnedSkillPrompt(BrowserTaskInput input, BrowserTaskState state, Skill? existing)
    {
        var sb = new StringBuilder();
        sb.Append("Intent: ").AppendLine(input.Intent);
        sb.Append("Allowed hosts: ").AppendLine(string.Join(", ", input.Allowlist!.Patterns));
        sb.Append("Navigations (in order): ").AppendLine(string.Join(" → ", state.Navigations.Select(u => u.ToString())));
        sb.Append("Tool calls: ").Append(state.Steps).AppendLine(".");
        if (!string.IsNullOrWhiteSpace(state.Summary))
            sb.Append("Final summary: ").AppendLine(state.Summary);
        if (!string.IsNullOrWhiteSpace(state.Result))
            sb.Append("Final result: ").AppendLine(state.Result);
        if (existing is not null)
        {
            sb.AppendLine();
            sb.AppendLine("A prior version of this skill exists — refine rather than rewrite when the content overlaps:");
            sb.AppendLine(existing.Content);
        }
        return sb.ToString();
    }

    private static (string Summary, string Content) SplitSummaryAndContent(string text)
    {
        var lines = text.Split('\n');
        var summary = lines[0].Trim();
        if (summary.Length > 200)
            summary = summary[..200];
        var content = lines.Length > 1
            ? string.Join('\n', lines.Skip(1)).TrimStart('\n', '\r', ' ')
            : text;
        return (summary, string.IsNullOrWhiteSpace(content) ? text : content);
    }

    private static string Slugify(string intent)
    {
        var sb = new StringBuilder(capacity: Math.Min(intent.Length, 64));
        var lastDash = true;
        foreach (var ch in intent.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastDash = false;
            }
            else if (!lastDash && sb.Length < 64)
            {
                sb.Append('-');
                lastDash = true;
            }
            if (sb.Length >= 64) break;
        }
        var slug = sb.ToString().Trim('-');
        return string.IsNullOrEmpty(slug) ? "task" : slug;
    }

    private static AgentTaskResult BuildResult(
        AgentTaskRequest request,
        BrowserTaskInput input,
        BrowserTaskState state)
    {
        // Structured JSON payload so callers (usually other agents) can parse
        // success vs. failure reliably; the summary field is the primary
        // human-readable signal.
        var payload = new
        {
            status = state.IsDone ? "done" : state.IsFailed ? "failed" : "incomplete",
            summary = state.IsDone
                ? state.Summary
                : state.IsFailed
                    ? state.FailureReason
                    : $"Task did not terminate within {input.MaxSteps} steps / {input.MaxSeconds}s.",
            result = state.Result,
            steps = state.Steps,
            navigations = state.Navigations.Select(u => u.ToString()).ToArray()
        };
        return CapabilityResult.Completed(request, JsonSerializer.Serialize(payload, SerializerOptions));
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false
    };
}
