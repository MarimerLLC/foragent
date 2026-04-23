using System.Text.Json;
using Foragent.Capabilities.BrowserTask;
using RockBot.A2A;

namespace Foragent.Capabilities.Forms;

/// <summary>
/// Parses input for <c>execute-form-batch</c> (spec §5.2, phase-3 of §5.5).
///
/// Shape (JSON in the first text part — metadata pass-through doesn't fit
/// multi-row shapes):
/// <list type="bullet">
///   <item><c>schemaRef</c> — optional. Skill name produced by <c>learn-form-schema</c>.</item>
///   <item><c>schema</c> — optional. Inline <see cref="FormSchema"/> JSON.</item>
///   <item><c>rows</c> — required, non-empty. Each row is an object keyed by field name.</item>
///   <item><c>allowedHosts</c> — required. Host allowlist (spec §7.1).</item>
///   <item><c>credentialId</c> — optional. Resolved and discarded (reserved for future auth).</item>
///   <item><c>mode</c> — optional. <c>abort-on-first</c> (default, spec open-question #8 resolution) or <c>continue</c>.</item>
///   <item><c>successIndicator</c> — optional CSS selector that signals successful submission.</item>
/// </list>
/// Exactly one of <c>schemaRef</c> and <c>schema</c> must be present.
/// </summary>
internal readonly record struct ExecuteFormBatchInput(
    string? SchemaRef,
    FormSchema? InlineSchema,
    IReadOnlyList<IReadOnlyDictionary<string, string>>? Rows,
    HostAllowlist? Allowlist,
    string? CredentialId,
    ExecuteFormBatchMode Mode,
    string? SuccessIndicator,
    string? Error)
{
    public static ExecuteFormBatchInput Parse(AgentTaskRequest request)
    {
        var text = request.Message.Parts
            .Where(p => p.Kind == "text")
            .Select(p => p.Text)
            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))
            ?.Trim();

        if (string.IsNullOrEmpty(text) || !text.StartsWith('{'))
            return Fail("Input must be a JSON object with rows, allowedHosts, and either schemaRef or schema.");

        string? schemaRef;
        FormSchema? inlineSchema = null;
        List<IReadOnlyDictionary<string, string>>? rows = null;
        List<string>? allowedHosts = null;
        string? credentialId;
        string? modeRaw;
        string? successIndicator;

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            schemaRef = root.TryGetProperty("schemaRef", out var sr) ? sr.GetString() : null;
            if (root.TryGetProperty("schema", out var s) && s.ValueKind == JsonValueKind.Object)
            {
                try
                {
                    inlineSchema = JsonSerializer.Deserialize<FormSchema>(s.GetRawText(), FormSchema.SerializerOptions);
                }
                catch (JsonException ex)
                {
                    return Fail($"Inline 'schema' is not a valid FormSchema: {ex.Message}");
                }
            }
            credentialId = root.TryGetProperty("credentialId", out var c) ? c.GetString() : null;
            modeRaw = root.TryGetProperty("mode", out var m) ? m.GetString() : null;
            successIndicator = root.TryGetProperty("successIndicator", out var si) ? si.GetString() : null;

            if (root.TryGetProperty("rows", out var r) && r.ValueKind == JsonValueKind.Array)
            {
                rows = [];
                var index = 0;
                foreach (var item in r.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        return Fail($"Row {index} is not an object.");
                    var dict = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var prop in item.EnumerateObject())
                    {
                        dict[prop.Name] = prop.Value.ValueKind switch
                        {
                            JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                            JsonValueKind.Number => prop.Value.ToString(),
                            JsonValueKind.True => "true",
                            JsonValueKind.False => "false",
                            JsonValueKind.Null => string.Empty,
                            _ => prop.Value.GetRawText()
                        };
                    }
                    rows.Add(dict);
                    index++;
                }
            }

            if (root.TryGetProperty("allowedHosts", out var h) && h.ValueKind == JsonValueKind.Array)
                allowedHosts = [.. h.EnumerateArray().Select(e => e.GetString() ?? string.Empty)];
        }
        catch (JsonException ex)
        {
            return Fail($"Input JSON is malformed: {ex.Message}");
        }

        if (schemaRef is null && inlineSchema is null)
            return Fail("Provide either 'schemaRef' (a skill name) or inline 'schema'.");
        if (schemaRef is not null && inlineSchema is not null)
            return Fail("Provide only one of 'schemaRef' or 'schema', not both.");
        if (rows is null || rows.Count == 0)
            return Fail("Missing 'rows' — must be a non-empty array of objects.");
        if (allowedHosts is null || allowedHosts.Count == 0)
            return Fail("Missing 'allowedHosts' — execute-form-batch requires an explicit allowlist (spec §7.1).");

        HostAllowlist allowlist;
        try
        {
            allowlist = HostAllowlist.Parse(allowedHosts);
        }
        catch (ArgumentException ex)
        {
            return Fail(ex.Message);
        }

        ExecuteFormBatchMode mode = ExecuteFormBatchMode.AbortOnFirst;
        if (modeRaw is not null)
        {
            mode = modeRaw.Trim().ToLowerInvariant() switch
            {
                "abort-on-first" or "" or null => ExecuteFormBatchMode.AbortOnFirst,
                "continue" => ExecuteFormBatchMode.Continue,
                _ => ExecuteFormBatchMode.Unknown
            };
            if (mode == ExecuteFormBatchMode.Unknown)
                return Fail($"Unknown 'mode' '{modeRaw}'. Valid values: 'abort-on-first' (default), 'continue'.");
        }

        if (inlineSchema is not null && !allowlist.IsAllowed(new Uri(inlineSchema.Url)))
            return Fail($"Inline schema URL '{inlineSchema.Url}' is not in the allowlist.");

        return new ExecuteFormBatchInput(schemaRef, inlineSchema, rows, allowlist, credentialId, mode, successIndicator, null);
    }

    private static ExecuteFormBatchInput Fail(string message) =>
        new(null, null, null, null, null, ExecuteFormBatchMode.AbortOnFirst, null, message);
}

internal enum ExecuteFormBatchMode
{
    AbortOnFirst,
    Continue,
    Unknown
}
