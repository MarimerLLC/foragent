using Foragent.Browser;
using Microsoft.Extensions.Logging;
using RockBot.A2A;

namespace Foragent.Capabilities;

/// <summary>
/// Dispatches incoming A2A task requests to the matching Foragent capability by skill id.
/// Step 2 still only ships <c>fetch-page-title</c>; the switch grows as new capabilities land.
/// </summary>
public sealed class ForagentTaskHandler(
    IBrowserSessionFactory browserFactory,
    ILogger<ForagentTaskHandler> logger) : IAgentTaskHandler
{
    public const string FetchPageTitleSkillId = "fetch-page-title";

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
            await using var session = await browserFactory.CreateSessionAsync(ct);
            var title = await session.FetchPageTitleAsync(uri, ct);
            var text = string.IsNullOrEmpty(title) ? "(no title)" : title;

            logger.LogInformation("Fetched title from {Url}: {Title}", uri, text);
            return Completed(request, text);
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
