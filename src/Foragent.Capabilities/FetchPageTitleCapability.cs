using Foragent.Browser;
using Microsoft.Extensions.Logging;
using RockBot.A2A;

namespace Foragent.Capabilities;

public sealed class FetchPageTitleCapability(
    IBrowserSessionFactory browserFactory,
    ILogger<FetchPageTitleCapability> logger) : ICapability
{
    public static AgentSkill SkillDefinition { get; } = new()
    {
        Id = "fetch-page-title",
        Name = "Fetch Page Title",
        Description = "Navigate to a URL with a real browser and return the contents of its <title> element."
    };

    public string SkillId => SkillDefinition.Id;
    public AgentSkill Skill => SkillDefinition;

    public async Task<AgentTaskResult> ExecuteAsync(AgentTaskRequest request, AgentTaskContext context)
    {
        var ct = context.MessageContext.CancellationToken;
        var (url, _) = CapabilityInput.Parse(request);

        if (url is null)
            return CapabilityResult.Error(request, "Provide an absolute http(s) URL as the task message.");

        try
        {
            await using var session = await browserFactory.CreateSessionAsync(ct);
            var title = await session.FetchPageTitleAsync(url, ct);
            var text = string.IsNullOrEmpty(title) ? "(no title)" : title;

            logger.LogInformation("Fetched title from {Url}: {Title}", url, text);
            return CapabilityResult.Completed(request, text);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Fetch failed for {Url}", url);
            return CapabilityResult.Error(request, $"Fetch failed: {ex.Message}");
        }
    }
}
