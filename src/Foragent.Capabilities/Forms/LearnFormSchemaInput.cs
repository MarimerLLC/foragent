using System.Text.Json;
using Foragent.Capabilities.BrowserTask;
using RockBot.A2A;

namespace Foragent.Capabilities.Forms;

/// <summary>
/// Parses input for the <c>learn-form-schema</c> capability (spec §5.2,
/// phase-1 of the learn→review→execute pattern in §5.5).
///
/// Shape (JSON in the first text part, field-by-field metadata overrides):
/// <list type="bullet">
///   <item><c>url</c> — required. Absolute http(s) URL of the page hosting the form.</item>
///   <item><c>allowedHosts</c> — required. Host allowlist (spec §7.1). Empty rejects.</item>
///   <item><c>formSelector</c> — optional CSS selector for the form. Defaults to the first <c>&lt;form&gt;</c>.</item>
///   <item><c>credentialId</c> — optional; resolved and discarded (reserved for future auth).</item>
///   <item><c>skillName</c> — optional override for the generated skill name; defaults to <c>sites/{host}/forms/{slug}</c>.</item>
///   <item><c>intent</c> — optional free-form description stored in the skill primer.</item>
/// </list>
/// </summary>
internal readonly record struct LearnFormSchemaInput(
    Uri? Url,
    HostAllowlist? Allowlist,
    string? FormSelector,
    string? CredentialId,
    string? SkillName,
    string? Intent,
    string? Error)
{
    public static LearnFormSchemaInput Parse(AgentTaskRequest request)
    {
        string? url = null;
        string? formSelector = null;
        string? credentialId = null;
        string? skillName = null;
        string? intent = null;
        List<string>? allowedHosts = null;

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
                if (root.TryGetProperty("url", out var u)) url = u.GetString();
                if (root.TryGetProperty("formSelector", out var fs)) formSelector = fs.GetString();
                if (root.TryGetProperty("credentialId", out var c)) credentialId = c.GetString();
                if (root.TryGetProperty("skillName", out var sn)) skillName = sn.GetString();
                if (root.TryGetProperty("intent", out var it)) intent = it.GetString();
                if (root.TryGetProperty("allowedHosts", out var h) && h.ValueKind == JsonValueKind.Array)
                    allowedHosts = [.. h.EnumerateArray().Select(e => e.GetString() ?? string.Empty)];
            }
            catch (JsonException)
            {
                return Fail("Input must be a JSON object with at least 'url' and 'allowedHosts'.");
            }
        }

        url = ReadMetadata(request, "url") ?? url;
        formSelector = ReadMetadata(request, "formSelector") ?? formSelector;
        credentialId = ReadMetadata(request, "credentialId") ?? credentialId;
        skillName = ReadMetadata(request, "skillName") ?? skillName;
        intent = ReadMetadata(request, "intent") ?? intent;
        var hostsCsv = ReadMetadata(request, "allowedHosts");
        if (hostsCsv is not null)
            allowedHosts = [.. hostsCsv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];

        if (string.IsNullOrWhiteSpace(url))
            return Fail("Missing 'url' — the page hosting the form.");

        if (allowedHosts is null || allowedHosts.Count == 0)
            return Fail("Missing 'allowedHosts' — learn-form-schema requires an explicit allowlist (spec §7.1).");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsedUrl) ||
            (parsedUrl.Scheme != Uri.UriSchemeHttp && parsedUrl.Scheme != Uri.UriSchemeHttps))
            return Fail($"'url' must be an absolute http(s) URL; got '{url}'.");

        HostAllowlist allowlist;
        try
        {
            allowlist = HostAllowlist.Parse(allowedHosts);
        }
        catch (ArgumentException ex)
        {
            return Fail(ex.Message);
        }

        if (!allowlist.IsAllowed(parsedUrl))
            return Fail($"URL host '{parsedUrl.Host}' is not in the allowlist.");

        return new LearnFormSchemaInput(parsedUrl, allowlist, formSelector, credentialId, skillName, intent, null);
    }

    private static LearnFormSchemaInput Fail(string message) =>
        new(null, null, null, null, null, null, message);

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
