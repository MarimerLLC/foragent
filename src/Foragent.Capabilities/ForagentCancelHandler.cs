using Microsoft.Extensions.Logging;
using RockBot.A2A;
using RockBot.Host;
using RockBot.Messaging;

namespace Foragent.Capabilities;

/// <summary>
/// Replaces the framework's default <c>AgentTaskCancelHandler</c> (which
/// always replies <see cref="AgentTaskError.Codes.TaskNotCancelable"/>
/// because stateless agents can't do anything useful). Foragent is stateful
/// — a browser task is long-running and must respond to cancel — so this
/// handler cancels the linked <see cref="CancellationToken"/> held by
/// <see cref="InFlightTaskRegistry"/>.
///
/// Reply semantics: on a successful cancel we publish nothing here. The
/// running task's own terminal result (an <see cref="AgentTaskResult"/> in
/// an error/cancelled shape) will reach the caller's ReplyTo topic shortly
/// afterward as the task unwinds. On a missed cancel (no matching task),
/// we publish <see cref="AgentTaskError.Codes.TaskNotFound"/> so the caller
/// knows the cancel was a no-op.
/// </summary>
public sealed class ForagentCancelHandler(
    InFlightTaskRegistry inFlight,
    IMessagePublisher publisher,
    A2AOptions options,
    AgentIdentity agent,
    ILogger<ForagentCancelHandler> logger) : IMessageHandler<AgentTaskCancelRequest>
{
    public async Task HandleAsync(AgentTaskCancelRequest request, MessageHandlerContext context)
    {
        if (inFlight.TryCancel(request.TaskId))
        {
            logger.LogInformation("Cancel requested for in-flight task {TaskId} — cancellation issued.", request.TaskId);
            return;
        }

        logger.LogDebug("Cancel requested for task {TaskId}, but no in-flight registration exists.", request.TaskId);

        var replyTo = context.Envelope.ReplyTo ?? options.DefaultResultTopic;
        var error = new AgentTaskError
        {
            TaskId = request.TaskId,
            ContextId = request.ContextId,
            Code = AgentTaskError.Codes.TaskNotFound,
            Message = "No in-flight task with that id; nothing to cancel.",
            IsRetryable = false
        };
        var envelope = error.ToEnvelope<AgentTaskError>(
            source: agent.Name,
            correlationId: context.Envelope.CorrelationId);
        await publisher.PublishAsync(replyTo, envelope, context.CancellationToken);
    }
}
