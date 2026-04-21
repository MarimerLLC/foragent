using System.Text;
using Foragent.Browser;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.A2A;

namespace Foragent.Capabilities;

public sealed class ExtractStructuredDataCapability(
    IBrowserSessionFactory browserFactory,
    IChatClient chatClient,
    ILogger<ExtractStructuredDataCapability> logger) : ICapability
{
    public static AgentSkill SkillDefinition { get; } = new()
    {
        Id = "extract-structured-data",
        Name = "Extract Structured Data",
        Description = "Navigate to a URL and extract data matching a natural-language description, returning JSON."
    };

    // Keep the prompt short — the page content already dominates the token budget.
    private const string SystemPrompt = """
        You extract structured data from web pages on behalf of other agents.
        The user gives you (1) a description of what to extract and (2) a
        compact accessibility-tree or text rendering of a page. Respond with a
        single JSON object containing only the fields that answer the request.
        Use null for fields that are not present on the page. Do not wrap the
        JSON in code fences or prose.
        """;

    // Extraction calls are usually a few seconds. Cap snapshot size so we don't
    // blow past the model's context window on a page dump.
    private const int MaxSnapshotChars = 40_000;

    public string SkillId => SkillDefinition.Id;
    public AgentSkill Skill => SkillDefinition;

    public async Task<AgentTaskResult> ExecuteAsync(AgentTaskRequest request, AgentTaskContext context)
    {
        var ct = context.MessageContext.CancellationToken;
        var (url, description) = CapabilityInput.Parse(request);

        if (url is null)
            return CapabilityResult.Error(
                request,
                "Provide the target URL (as 'url' in the request payload).");

        if (string.IsNullOrWhiteSpace(description))
            return CapabilityResult.Error(
                request,
                "Provide a natural-language description of the data to extract.");

        PageSnapshot snapshot;
        try
        {
            await using var session = await browserFactory.CreateSessionAsync(ct);
            snapshot = await session.CapturePageSnapshotAsync(url, ct);
            logger.LogInformation(
                "Captured {Source} snapshot of {Url} ({Chars} chars)",
                snapshot.Source, snapshot.Url, snapshot.Content.Length);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Snapshot failed for {Url}", url);
            return CapabilityResult.Error(request, $"Fetch failed: {ex.Message}");
        }

        var prompt = BuildPrompt(snapshot, description!);

        try
        {
            var response = await chatClient.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, SystemPrompt),
                    new ChatMessage(ChatRole.User, prompt)
                ],
                new ChatOptions { ResponseFormat = ChatResponseFormat.Json },
                ct);

            var text = response.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(text))
                return CapabilityResult.Error(request, "LLM returned an empty response.");

            return CapabilityResult.Completed(request, text);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "LLM extraction failed for {Url}", url);
            return CapabilityResult.Error(request, $"Extraction failed: {ex.Message}");
        }
    }

    private static string BuildPrompt(PageSnapshot snapshot, string description)
    {
        var content = snapshot.Content.Length > MaxSnapshotChars
            ? snapshot.Content[..MaxSnapshotChars] + "\n…(truncated)"
            : snapshot.Content;

        var sb = new StringBuilder();
        sb.Append("Page URL: ").AppendLine(snapshot.Url.ToString());
        if (snapshot.Title is not null)
            sb.Append("Page title: ").AppendLine(snapshot.Title);
        sb.Append("Snapshot source: ").AppendLine(snapshot.Source.ToString());
        sb.AppendLine();
        sb.AppendLine("Description of data to extract:");
        sb.AppendLine(description);
        sb.AppendLine();
        sb.AppendLine("Page content:");
        sb.AppendLine(content);
        return sb.ToString();
    }
}
