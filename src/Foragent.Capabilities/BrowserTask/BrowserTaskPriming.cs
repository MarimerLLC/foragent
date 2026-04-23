using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Host;

namespace Foragent.Capabilities.BrowserTask;

/// <summary>
/// Retrieves learned site knowledge for a <c>browser-task</c> invocation and
/// formats it as a block for the planner's user prompt (spec §5.6). Queries
/// both <see cref="ISkillStore"/> and <see cref="ILongTermMemory"/> in
/// parallel; hybrid BM25 + vector when an <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>
/// is in DI, BM25-only otherwise.
///
/// Isolated from <see cref="BrowserTaskCapability"/> so tests can inject
/// fake stores without going through the capability's full execute path.
/// </summary>
public sealed class BrowserTaskPriming(
    ISkillStore skillStore,
    ILongTermMemory longTermMemory,
    IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator,
    ILogger<BrowserTaskPriming> logger)
{
    public const int MaxSkills = 5;
    public const int MaxMemories = 5;

    public async Task<string?> BuildAsync(
        string intent,
        HostAllowlist allowlist,
        CancellationToken cancellationToken)
    {
        var primaryHost = allowlist.Patterns
            .Select(p => p.TrimStart('*', '.'))
            .FirstOrDefault(p => !string.IsNullOrEmpty(p) && p != "*");
        var query = string.IsNullOrEmpty(primaryHost)
            ? intent
            : $"{intent} site:{primaryHost}";

        var embedding = await TryEmbedAsync(query, cancellationToken);

        var skillsTask = SafeSearchSkillsAsync(query, embedding, cancellationToken);
        var memoriesTask = SafeSearchMemoriesAsync(query, primaryHost, embedding, cancellationToken);
        await Task.WhenAll(skillsTask, memoriesTask);

        var skills = skillsTask.Result;
        var memories = memoriesTask.Result;

        if (skills.Count == 0 && memories.Count == 0)
            return null;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Known site knowledge (from prior runs and operator primers):");
        foreach (var s in skills)
        {
            sb.Append("- [skill] ").Append(s.Name).Append(": ").AppendLine(s.Summary);
            if (!string.IsNullOrWhiteSpace(s.Content))
            {
                sb.AppendLine(Indent(Trim(s.Content, 1500)));
            }
        }
        foreach (var m in memories)
        {
            sb.Append("- [memory] ").AppendLine(Trim(m.Content, 400));
        }
        sb.AppendLine("Treat these as hints, not ground truth — re-snapshot to confirm selectors and URLs.");
        return sb.ToString();
    }

    private async Task<float[]?> TryEmbedAsync(string query, CancellationToken ct)
    {
        if (embeddingGenerator is null)
            return null;
        try
        {
            var result = await embeddingGenerator.GenerateAsync([query], cancellationToken: ct);
            return result.Count > 0 ? result[0].Vector.ToArray() : null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Embedding generation failed; falling back to BM25-only retrieval.");
            return null;
        }
    }

    private async Task<IReadOnlyList<Skill>> SafeSearchSkillsAsync(
        string query, float[]? embedding, CancellationToken ct)
    {
        try
        {
            return await skillStore.SearchAsync(query, MaxSkills, ct, embedding);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Skill search failed; continuing without skill priming.");
            return [];
        }
    }

    private async Task<IReadOnlyList<MemoryEntry>> SafeSearchMemoriesAsync(
        string query, string? primaryHost, float[]? embedding, CancellationToken ct)
    {
        try
        {
            var criteria = new MemorySearchCriteria(
                Query: query,
                Category: primaryHost is null ? null : $"sites/{primaryHost}",
                Tags: [],
                CreatedAfter: null,
                CreatedBefore: null,
                MaxResults: MaxMemories,
                QueryEmbedding: embedding!);
            return await longTermMemory.SearchAsync(criteria, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Memory search failed; continuing without memory priming.");
            return [];
        }
    }

    private static string Trim(string text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max)
            return text;
        return text[..max] + "…";
    }

    private static string Indent(string text)
    {
        var lines = text.Split('\n');
        return string.Join('\n', lines.Select(l => "  " + l));
    }
}
