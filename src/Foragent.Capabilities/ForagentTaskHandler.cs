using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RockBot.A2A;

namespace Foragent.Capabilities;

/// <summary>
/// Dispatches incoming A2A task requests to the matching Foragent capability by skill id.
/// Step 1 only ships <c>fetch-page-title</c>; the switch grows as new capabilities land.
/// </summary>
public sealed partial class ForagentTaskHandler(
    HttpClient httpClient,
    ILogger<ForagentTaskHandler> logger) : IAgentTaskHandler
{
    public const string FetchPageTitleSkillId = "fetch-page-title";

    [GeneratedRegex("<title[^>]*>([^<]*)</title>", RegexOptions.IgnoreCase)]
    private static partial Regex TitleRegex();

    public async Task<AgentTaskResult> HandleTaskAsync(
        AgentTaskRequest request, AgentTaskContext context)
    {
        var ct = context.MessageContext.CancellationToken;

        logger.LogInformation("Handling task {TaskId} (skill={Skill})",
            request.TaskId, request.Skill);

        await context.PublishStatus(new AgentTaskStatusUpdate
        {
            TaskId = request.TaskId,
            ContextId = request.ContextId,
            State = AgentTaskState.Working
        }, ct);

        return request.Skill switch
        {
            FetchPageTitleSkillId => await FetchPageTitleAsync(request, ct),
            _ => Error(request, $"Unknown skill '{request.Skill}'.")
        };
    }

    private async Task<AgentTaskResult> FetchPageTitleAsync(
        AgentTaskRequest request, CancellationToken ct)
    {
        var input = request.Message.Parts
            .Where(p => p.Kind == "text")
            .Select(p => p.Text)
            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))
            ?.Trim();

        if (string.IsNullOrEmpty(input)
            || !Uri.TryCreate(input, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return Error(request, "Provide an absolute http(s) URL as the task message.");
        }

        try
        {
            using var response = await httpClient.GetAsync(uri, ct);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(ct);
            var match = TitleRegex().Match(html);
            var title = match.Success
                ? System.Net.WebUtility.HtmlDecode(match.Groups[1].Value).Trim()
                : "(no title)";

            logger.LogInformation("Fetched title from {Url}: {Title}", uri, title);
            return Completed(request, title);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Fetch failed for {Url}", uri);
            return Error(request, $"Fetch failed: {ex.Message}");
        }
    }

    private static AgentTaskResult Completed(AgentTaskRequest request, string text) => new()
    {
        TaskId = request.TaskId,
        ContextId = request.ContextId,
        State = AgentTaskState.Completed,
        Message = new AgentMessage
        {
            Role = "agent",
            Parts = [new AgentMessagePart { Kind = "text", Text = text }]
        }
    };

    private static AgentTaskResult Error(AgentTaskRequest request, string message) => new()
    {
        TaskId = request.TaskId,
        ContextId = request.ContextId,
        State = AgentTaskState.Completed,
        Message = new AgentMessage
        {
            Role = "agent",
            Parts = [new AgentMessagePart { Kind = "text", Text = message }]
        }
    };
}
