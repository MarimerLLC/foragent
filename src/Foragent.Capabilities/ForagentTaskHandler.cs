using Microsoft.Extensions.Logging;
using RockBot.A2A;

namespace Foragent.Capabilities;

/// <summary>
/// Pure dispatcher. Resolves the set of registered <see cref="ICapability"/>
/// instances and routes an incoming task to the one whose <see cref="ICapability.SkillId"/>
/// matches <see cref="AgentTaskRequest.Skill"/>. Unknown skills return a user-
/// facing error rather than throwing, so the A2A bridge still sees a result.
/// </summary>
public sealed class ForagentTaskHandler(
    IEnumerable<ICapability> capabilities,
    ILogger<ForagentTaskHandler> logger) : IAgentTaskHandler
{
    private readonly Dictionary<string, ICapability> _bySkill =
        capabilities.ToDictionary(c => c.SkillId, StringComparer.OrdinalIgnoreCase);

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

        if (!_bySkill.TryGetValue(request.Skill, out var capability))
        {
            var known = string.Join(", ", _bySkill.Keys.OrderBy(k => k));
            return CapabilityResult.Error(
                request,
                $"Unknown skill '{request.Skill}'. Known skills: {known}");
        }

        return await capability.ExecuteAsync(request, context);
    }
}
