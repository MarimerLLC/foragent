using RockBot.A2A;

namespace Foragent.Capabilities;

/// <summary>
/// A task-level verb the agent exposes over A2A. Each capability owns its own
/// skill metadata and execution logic. <see cref="ForagentTaskHandler"/>
/// resolves the full set of <see cref="ICapability"/> registrations at request
/// time and dispatches to the one matching <see cref="AgentTaskRequest.Skill"/>.
/// </summary>
public interface ICapability
{
    /// <summary>
    /// The id published on the agent card. Callers reference capabilities by this value.
    /// </summary>
    string SkillId { get; }

    /// <summary>
    /// Description of the skill for the A2A agent card. Must be a stable value —
    /// consumers cache the card and route by id.
    /// </summary>
    AgentSkill Skill { get; }

    /// <summary>
    /// Executes the capability. Inputs come from the request message; side
    /// effects (HTTP calls, browser navigation, LLM usage) live inside the
    /// implementation. Must return an <see cref="AgentTaskResult"/> — throwing
    /// leaves the A2A task without a reply.
    /// </summary>
    Task<AgentTaskResult> ExecuteAsync(AgentTaskRequest request, AgentTaskContext context);
}
