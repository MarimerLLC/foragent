using System.Text.Json;
using Foragent.Browser;
using Foragent.Capabilities.SitePosting;
using Foragent.Credentials;
using Microsoft.Extensions.Logging;
using RockBot.A2A;

namespace Foragent.Capabilities;

/// <summary>
/// Authenticates against a configured site and posts content (spec §5.1).
/// Site-specific work lives behind <see cref="ISitePoster"/>; this capability
/// handles input parsing, broker lookup, session creation, and error shaping.
/// Credential material never appears in the returned <see cref="AgentTaskResult"/>
/// (spec §6.1).
/// </summary>
public sealed class PostToSiteCapability(
    IBrowserSessionFactory browserFactory,
    ICredentialBroker credentialBroker,
    IEnumerable<ISitePoster> posters,
    ILogger<PostToSiteCapability> logger) : ICapability
{
    public static AgentSkill SkillDefinition { get; } = new()
    {
        Id = "post-to-site",
        Name = "Post to Site",
        Description = "Authenticate against a configured site (using a credential identifier) and publish a post. "
            + "Input: JSON {\"site\":\"bluesky\",\"credentialId\":\"...\",\"content\":\"...\"} "
            + "or metadata fields site / credentialId / content."
    };

    private readonly IReadOnlyDictionary<string, ISitePoster> _postersBySite =
        posters.ToDictionary(p => p.Site, StringComparer.OrdinalIgnoreCase);

    public string SkillId => SkillDefinition.Id;
    public AgentSkill Skill => SkillDefinition;

    public async Task<AgentTaskResult> ExecuteAsync(AgentTaskRequest request, AgentTaskContext context)
    {
        var ct = context.MessageContext.CancellationToken;
        var input = PostToSiteInput.Parse(request);

        if (input.Error is not null)
            return CapabilityResult.Error(request, input.Error);

        if (!_postersBySite.TryGetValue(input.Site!, out var poster))
        {
            var known = string.Join(", ", _postersBySite.Keys.OrderBy(k => k));
            return CapabilityResult.Error(
                request,
                $"No poster configured for site '{input.Site}'. Known sites: {known}");
        }

        CredentialReference credential;
        try
        {
            credential = await credentialBroker.ResolveAsync(input.CredentialId!, ct);
        }
        catch (CredentialNotFoundException ex)
        {
            logger.LogWarning("Credential '{CredentialId}' not found", ex.CredentialId);
            return CapabilityResult.Error(request, $"Credential '{ex.CredentialId}' is not configured.");
        }

        try
        {
            await using var session = await browserFactory.CreateSessionAsync(ct);
            await poster.PostAsync(session, credential, input.Content!, ct);
            return CapabilityResult.Completed(request, $"Posted to {poster.Site}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never echo exception messages verbatim — site posters should not
            // embed credentials in them, but belt-and-suspenders since these go
            // back to the caller. Log the full exception for operator debugging.
            logger.LogWarning(ex, "Post to {Site} failed for credential {CredentialId}",
                poster.Site, credential.Id);
            return CapabilityResult.Error(request, $"Post to {poster.Site} failed.");
        }
    }
}

/// <summary>
/// Parses the <c>post-to-site</c> input shape. Accepts either:
/// <list type="bullet">
///   <item>A JSON object in the first text part: <c>{"site":"...","credentialId":"...","content":"..."}</c>.</item>
///   <item>Individual fields via message or request metadata (rockbot 0.8.5+):
///   <c>site</c>, <c>credentialId</c>, <c>content</c>. Metadata overrides JSON when both are present.</item>
/// </list>
/// No URL-extraction fallback — post-to-site is structured enough that bare
/// text input would be ambiguous. Unparseable input yields <see cref="Error"/>.
/// </summary>
internal readonly record struct PostToSiteInput(
    string? Site, string? CredentialId, string? Content, string? Error)
{
    public static PostToSiteInput Parse(AgentTaskRequest request)
    {
        string? site = null;
        string? credentialId = null;
        string? content = null;

        var text = request.Message.Parts
            .Where(p => p.Kind == "text")
            .Select(p => p.Text)
            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))
            ?.Trim();

        if (!string.IsNullOrEmpty(text) && text.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.TryGetProperty("site", out var s)) site = s.GetString();
                if (root.TryGetProperty("credentialId", out var c)) credentialId = c.GetString();
                if (root.TryGetProperty("content", out var p)) content = p.GetString();
            }
            catch (JsonException)
            {
                return new PostToSiteInput(null, null, null,
                    "Input must be a JSON object with site, credentialId, and content fields.");
            }
        }

        site = ReadMetadata(request, "site") ?? site;
        credentialId = ReadMetadata(request, "credentialId") ?? credentialId;
        content = ReadMetadata(request, "content") ?? content;

        if (string.IsNullOrWhiteSpace(site))
            return new PostToSiteInput(null, null, null, "Missing 'site' (e.g. 'bluesky').");
        if (string.IsNullOrWhiteSpace(credentialId))
            return new PostToSiteInput(null, null, null, "Missing 'credentialId'.");
        if (string.IsNullOrWhiteSpace(content))
            return new PostToSiteInput(null, null, null, "Missing 'content'.");

        return new PostToSiteInput(site, credentialId, content, null);
    }

    private static string? ReadMetadata(AgentTaskRequest request, string key)
    {
        if (request.Message.Metadata is not null
            && request.Message.Metadata.TryGetValue(key, out var msgValue)
            && !string.IsNullOrWhiteSpace(msgValue))
        {
            return msgValue;
        }
        if (request.Metadata is not null
            && request.Metadata.TryGetValue(key, out var reqValue)
            && !string.IsNullOrWhiteSpace(reqValue))
        {
            return reqValue;
        }
        return null;
    }
}
