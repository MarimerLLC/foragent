using System.Text.Json;
using RockBot.A2A;

namespace Foragent.Capabilities.BrowserTask;

/// <summary>
/// Parses the <c>browser-task</c> input shape (spec §5.2).
///
/// Accepts either a JSON object in the first text part or field-by-field
/// metadata on the message/request. Metadata overrides JSON when both are
/// present. Shape:
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
