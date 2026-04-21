using System.Text.Json;
using System.Text.RegularExpressions;
using RockBot.A2A;

namespace Foragent.Capabilities;

/// <summary>
/// Helpers for pulling structured input out of an <see cref="AgentTaskRequest"/>.
/// Foragent's capability contract (spec §5.3 — capabilities own their input shape):
/// <list type="bullet">
///   <item>Target URL in <c>url</c> metadata</item>
///   <item>Natural-language description in the first text part, or <c>description</c> metadata</item>
/// </list>
/// Parse order, most-structured-first:
/// <list type="number">
///   <item>Message or request metadata <c>url</c>/<c>description</c> (rockbot 0.8.5+)</item>
///   <item>JSON blob in a text part: <c>{"url":"...","description":"..."}</c></item>
///   <item>Bare URL text part (step 1/2 callers)</item>
///   <item>First <c>http(s)://…</c> URL embedded in free-form text (handles
///   LLM-wrapped prompts like "please fetch the title of https://example.com")</item>
/// </list>
/// </summary>
internal static partial class CapabilityInput
{
    // Matches the first http(s) URL inside a larger string. Stops at whitespace
    // or common terminators so trailing punctuation doesn't poison the URL.
    [GeneratedRegex(@"https?://[^\s<>""'\)\]\}]+", RegexOptions.IgnoreCase)]
    private static partial Regex EmbeddedUrlRegex();

    /// <summary>
    /// Parses a capability request into (url, description). Either field may be
    /// null if the caller didn't supply it — capabilities decide which are
    /// required. Parse order: (1) JSON shim, (2) bare URL, (3) first embedded
    /// URL in free-form text — so LLM-authored requests like "please fetch
    /// the title of https://example.com" are accepted. If (3) is used and the
    /// text carries more than just the URL, the surrounding text becomes the
    /// description.
    /// </summary>
    public static (Uri? Url, string? Description) Parse(AgentTaskRequest request)
    {
        // 1. Metadata (rockbot 0.8.5+). Message-level wins over request-level
        //    when both are present — the URL is about the message content.
        var metaUrl = ReadMetadata(request, "url");
        var metaDescription = ReadMetadata(request, "description");
        var urlFromMetadata = TryParseAbsoluteHttpUri(metaUrl);
        if (urlFromMetadata is not null)
            return (urlFromMetadata, string.IsNullOrWhiteSpace(metaDescription) ? null : metaDescription);

        var text = request.Message.Parts
            .Where(p => p.Kind == "text")
            .Select(p => p.Text)
            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))
            ?.Trim();

        if (string.IsNullOrEmpty(text))
            return (null, string.IsNullOrWhiteSpace(metaDescription) ? null : metaDescription);

        // JSON shim: {"url": "...", "description": "..."}
        if (text.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                var urlStr = root.TryGetProperty("url", out var u) ? u.GetString() : null;
                var description = root.TryGetProperty("description", out var d) ? d.GetString() : null;
                return (TryParseAbsoluteHttpUri(urlStr), description);
            }
            catch (JsonException)
            {
                // Not valid JSON — fall through to bare-URL handling.
            }
        }

        // Bare URL — covers step 1/2 callers and simple single-input skills.
        var bare = TryParseAbsoluteHttpUri(text);
        if (bare is not null)
            return (bare, null);

        // Fall back to extracting the first http(s) URL embedded in free-form
        // text. Anything around it becomes the description for capabilities
        // that want one.
        var match = EmbeddedUrlRegex().Match(text);
        if (match.Success)
        {
            var embedded = TryParseAbsoluteHttpUri(TrimUrlTerminators(match.Value));
            if (embedded is not null)
            {
                var remainder = (text[..match.Index] + text[(match.Index + match.Length)..]).Trim();
                return (embedded, string.IsNullOrWhiteSpace(remainder) ? null : remainder);
            }
        }

        return (null, null);
    }

    private static string TrimUrlTerminators(string value) =>
        value.TrimEnd('.', ',', ';', ':', '!', '?');

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

    private static Uri? TryParseAbsoluteHttpUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        return uri;
    }
}
