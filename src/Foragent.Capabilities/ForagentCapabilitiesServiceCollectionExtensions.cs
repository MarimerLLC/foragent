using Foragent.Capabilities.BrowserTask;
using Foragent.Capabilities.Forms;
using Microsoft.Extensions.DependencyInjection;
using RockBot.A2A;

namespace Foragent.Capabilities;

public static class ForagentCapabilitiesServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Foragent capability set plus the dispatcher. Add new
    /// capabilities in both places: the DI registration below and
    /// <see cref="ForagentCapabilities.Skills"/> so the agent card advertises them.
    /// </summary>
    public static IServiceCollection AddForagentCapabilities(this IServiceCollection services)
    {
        services.AddScoped<ICapability, BrowserTaskCapability>();
        services.AddScoped<ICapability, LearnFormSchemaCapability>();
        services.AddScoped<ICapability, ExecuteFormBatchCapability>();
        services.AddScoped<BrowserTaskPriming>();
        services.AddScoped<FormSchemaEnricher>();
        services.AddScoped<IAgentTaskHandler, ForagentTaskHandler>();
        // Process-wide registry: the cancel message is dispatched in a
        // different DI scope than the task request, so the registry must
        // outlive scopes.
        services.AddSingleton<InFlightTaskRegistry>();
        return services;
    }
}

/// <summary>
/// Single source of truth for the skills Foragent advertises. The dispatcher
/// in <see cref="ForagentTaskHandler"/> reaches the instances via DI; the agent
/// card at startup reads this static list to avoid duplicating skill metadata
/// in <c>appsettings.json</c>.
/// </summary>
public static class ForagentCapabilities
{
    public static IReadOnlyList<AgentSkill> Skills { get; } =
    [
        BrowserTaskCapability.SkillDefinition,
        LearnFormSchemaCapability.SkillDefinition,
        ExecuteFormBatchCapability.SkillDefinition
    ];
}
