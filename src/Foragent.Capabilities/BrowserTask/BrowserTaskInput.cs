using System.Text.Json;
using RockBot.A2A;

namespace Foragent.Capabilities.BrowserTask;

/// <summary>
/// Parses the <c>browser-task</c> input shape (spec §5.2).
///
/// Accepts structured input in three forms, checked in order:
/// <list type="number">
///   <item>An A2A <c>DataPart</c> (message part with <c>Kind = "data"</c>,
///   <c>MimeType = "application/json"</c>) carrying the JSON object. This
///   is the preferred shape and the one RockBot's <c>invoke_agent</c> tool
///   produces when the caller fills its <c>data</c> parameter.</item>
///   <item>A JSON object in the first text part (curl callers, agents that
///   don't use data parts).</item>
///   <item>Field-by-field metadata on the message/request. Metadata
///   overrides fields from the JSON source when both are present.</item>
/// </list>
/// Shape:
/// <list type="bullet">
///   <item><c>intent</c> — required. Free-form description of what to do.</item>
///   <item><c>allowedHosts</c> — required. Array of host patterns. Empty rejects.</item>
///   <item><c>url</c> — optional starting URL (string).</item>
///   <item><c>credentialId</c> — optional credential reference.</item>
///   <item><c>maxSteps</c> — optional int; default 60, max 150.</item>
///   <item><c>maxSeconds</c> — optional int; default 120, max 600.</item>
/// </list>
/// </summary>
internal readonly record struct BrowserTaskInput(
    string? Intent,
    Uri? Url,
    string? CredentialId,
    HostAllowlist? Allowlist,
    int MaxSteps,
    int MaxSeconds,
    string? Error)
{
    public const int DefaultMaxSteps = 60;
    public const int CeilingMaxSteps = 150;
    public const int DefaultMaxSeconds = 120;
    public const int CeilingMaxSeconds = 600;

    public static BrowserTaskInput Parse(AgentTaskRequest request)
    {
        string? intent = null;
        string? url = null;
        string? credentialId = null;
        List<string>? allowedHosts = null;
        int? maxSteps = null;
        int? maxSeconds = null;

        // Preferred shape: RockBot's invoke_agent sends structured fields as
        // a DataPart alongside the prose text part. Prefer it when present so
        // the text part can stay human-readable.
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
                if (root.TryGetProperty("intent", out var i)) intent = i.GetString();
                if (root.TryGetProperty("url", out var u)) url = u.GetString();
                if (root.TryGetProperty("credentialId", out var c)) credentialId = c.GetString();
                if (root.TryGetProperty("allowedHosts", out var h) && h.ValueKind == JsonValueKind.Array)
                    allowedHosts = [.. h.EnumerateArray().Select(e => e.GetString() ?? string.Empty)];
                if (root.TryGetProperty("maxSteps", out var ms) && ms.TryGetInt32(out var msv))
                    maxSteps = msv;
                if (root.TryGetProperty("maxSeconds", out var mt) && mt.TryGetInt32(out var mtv))
                    maxSeconds = mtv;
            }
            catch (JsonException)
            {
                return Fail("Input must be a JSON object with intent, allowedHosts, and optional url/credentialId/maxSteps/maxSeconds.");
            }

            // When a DataPart carried the structured fields, the text part is
            // the human-readable prose from the caller — use it as a fallback
            // intent if the data part didn't supply one.
            if (string.IsNullOrWhiteSpace(intent) && !string.IsNullOrEmpty(text) && !text.StartsWith('{'))
                intent = text;
        }
        else if (!string.IsNullOrEmpty(text))
        {
            intent = text;
        }

        intent = ReadMetadata(request, "intent") ?? intent;
        url = ReadMetadata(request, "url") ?? url;
        credentialId = ReadMetadata(request, "credentialId") ?? credentialId;
        var hostsCsv = ReadMetadata(request, "allowedHosts");
        if (hostsCsv is not null)
            allowedHosts = [.. hostsCsv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];

        if (string.IsNullOrWhiteSpace(intent))
            return Fail("Missing 'intent' — a natural-language description of what to do.");

        if (allowedHosts is null || allowedHosts.Count == 0)
            return Fail("Missing 'allowedHosts' — browser-task requires an explicit allowlist (spec §7.1). Use ['*'] to accept any host.");

        HostAllowlist allowlist;
        try
        {
            allowlist = HostAllowlist.Parse(allowedHosts);
        }
        catch (ArgumentException ex)
        {
            return Fail(ex.Message);
        }

        Uri? parsedUrl = null;
        if (!string.IsNullOrWhiteSpace(url))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out parsedUrl) ||
                (parsedUrl.Scheme != Uri.UriSchemeHttp && parsedUrl.Scheme != Uri.UriSchemeHttps))
                return Fail($"'url' must be an absolute http(s) URL; got '{url}'.");
            if (!allowlist.IsAllowed(parsedUrl))
                return Fail($"Starting URL host '{parsedUrl.Host}' is not in the allowlist.");
        }

        var steps = Clamp(maxSteps ?? DefaultMaxSteps, 1, CeilingMaxSteps);
        var seconds = Clamp(maxSeconds ?? DefaultMaxSeconds, 1, CeilingMaxSeconds);

        return new BrowserTaskInput(intent, parsedUrl, credentialId, allowlist, steps, seconds, null);
    }

    private static BrowserTaskInput Fail(string message) =>
        new(null, null, null, null, DefaultMaxSteps, DefaultMaxSeconds, message);

    private static int Clamp(int value, int min, int max) =>
        value < min ? min : value > max ? max : value;

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
