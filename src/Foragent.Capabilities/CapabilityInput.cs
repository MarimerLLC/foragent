using System.Text.Json;
using RockBot.A2A;

namespace Foragent.Capabilities;

/// <summary>
/// Helpers for pulling structured input out of an <see cref="AgentTaskRequest"/>.
/// Foragent's capability contract (spec §5.3 — capabilities own their input shape):
/// <list type="bullet">
///   <item>Target URL in <c>url</c> metadata</item>
///   <item>Natural-language description in the first text part</item>
/// </list>
/// The metadata path is not yet available because the RockBot A2A bridge drops
/// request/message metadata on the floor (rockbot#281). Until that lands we
/// accept a JSON shim: a single text part of the shape
/// <c>{"url":"...","description":"..."}</c>. Capabilities that only need a URL
/// also accept a bare URL as the text (back-compat with step 1/2 callers).
/// When rockbot#281 ships, swap the shim path for real metadata reads and the
/// capability contracts stay stable.
/// </summary>
internal static class CapabilityInput
{
    /// <summary>
    /// Parses a capability request into (url, description). Either field may be
    /// null if the caller didn't supply it — capabilities decide which are
    /// required.
    /// </summary>
    public static (Uri? Url, string? Description) Parse(AgentTaskRequest request)
    {
        var text = request.Message.Parts
            .Where(p => p.Kind == "text")
            .Select(p => p.Text)
            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))
            ?.Trim();

        if (string.IsNullOrEmpty(text))
            return (null, null);

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

        // Bare URL (step 1/2 callers and simple single-input capabilities).
        return (TryParseAbsoluteHttpUri(text), null);
    }

    private static Uri? TryParseAbsoluteHttpUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        return uri;
    }
}
