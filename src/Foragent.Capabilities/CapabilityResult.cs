using RockBot.A2A;

namespace Foragent.Capabilities;

/// <summary>
/// Shared constructors for the two <see cref="AgentTaskResult"/> shapes every
/// capability returns: a successful completion with a text payload, or a
/// user-facing error that still reports <see cref="AgentTaskState.Completed"/>
/// (A2A's <c>Failed</c> is reserved for infrastructure errors, not expected
/// per-request problems like "bad input").
/// </summary>
internal static class CapabilityResult
{
    public static AgentTaskResult Completed(AgentTaskRequest request, string text) => new()
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

    public static AgentTaskResult Error(AgentTaskRequest request, string message) =>
        Completed(request, message);
}
