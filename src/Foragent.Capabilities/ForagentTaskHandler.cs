using Microsoft.Extensions.Logging;
using RockBot.A2A;
using RockBot.Host;

namespace Foragent.Capabilities;

/// <summary>
/// Pure dispatcher. Resolves the set of registered <see cref="ICapability"/>
/// instances and routes an incoming task to the one whose <see cref="ICapability.SkillId"/>
/// matches <see cref="AgentTaskRequest.Skill"/>. Unknown skills return a user-
/// facing error rather than throwing, so the A2A bridge still sees a result.
///
/// Registers each in-flight task with <see cref="InFlightTaskRegistry"/> and
/// wraps the context so the capability's <see cref="CancellationToken"/> is
/// linked to both the framework's message CT and the registry's external
/// cancel trigger — letting <c>agent.task.cancel.Foragent</c> messages stop
/// long-running browser work mid-flight.
/// </summary>
public sealed class ForagentTaskHandler(
    IEnumerable<ICapability> capabilities,
    InFlightTaskRegistry inFlight,
    ILogger<ForagentTaskHandler> logger) : IAgentTaskHandler
{
    private readonly Dictionary<string, ICapability> _bySkill =
        capabilities.ToDictionary(c => c.SkillId, StringComparer.OrdinalIgnoreCase);

    public async Task<AgentTaskResult> HandleTaskAsync(
        AgentTaskRequest request, AgentTaskContext context)
    {
        var parentCt = context.MessageContext.CancellationToken;
        var linkedCt = inFlight.Register(request.TaskId, parentCt);

        try
        {
            logger.LogInformation("Handling task {TaskId} (skill={Skill})",
                request.TaskId, request.Skill);

            await context.PublishStatus(new AgentTaskStatusUpdate
            {
                TaskId = request.TaskId,
                ContextId = request.ContextId,
                State = AgentTaskState.Working
            }, linkedCt);

            if (!_bySkill.TryGetValue(request.Skill, out var capability))
            {
                var known = string.Join(", ", _bySkill.Keys.OrderBy(k => k));
                return CapabilityResult.Error(
                    request,
                    $"Unknown skill '{request.Skill}'. Known skills: {known}");
            }

            // Wrap the context so the capability observes the linked token
            // instead of the raw message CT. Capabilities keep reading
            // context.MessageContext.CancellationToken — no signature change.
            var cancellableContext = new AgentTaskContext
            {
                MessageContext = new MessageHandlerContext
                {
                    Envelope = context.MessageContext.Envelope,
                    Agent = context.MessageContext.Agent,
                    Services = context.MessageContext.Services,
                    CancellationToken = linkedCt
                },
                PublishStatus = context.PublishStatus
            };

            return await capability.ExecuteAsync(request, cancellableContext);
        }
        finally
        {
            inFlight.Remove(request.TaskId);
        }
    }
}
