using System.Text.Json;
using Foragent.Capabilities.BrowserTask;
using RockBot.A2A;

namespace Foragent.Capabilities.Forms;

/// <summary>
/// Parses input for the <c>learn-form-schema</c> capability (spec §5.2,
/// phase-1 of the learn→review→execute pattern in §5.5).
///
/// Accepts the structured input in three forms: preferred is an A2A
/// <c>DataPart</c> (<c>Kind = "data"</c>, <c>MimeType = "application/json"</c>)
/// — the shape RockBot's <c>invoke_agent</c> produces when the caller fills
/// its <c>data</c> parameter. Falls back to a JSON object in the first text
/// part for curl callers. Field-by-field metadata overrides either source.
///
/// Shape:
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

        var dataJson = request.Message.Parts
            .Where(p => p.Kind == "data" && !string.IsNullOrWhiteSpace(p.Data))
            .Select(p => p.Data)
            .FirstOrDefault();

        var text = request.Message.Parts
            .Where(p => p.Kind == "text")
            .Select(p => p.Text)
            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))
            ?.Trim();

        string? jsonPayload = dataJson ?? (!string.IsNullOrEmpty(text) && text.StartsWith('{') ? text : null);

        if (jsonPayload is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonPayload);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return Fail("Structured input must be a JSON object.");
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
