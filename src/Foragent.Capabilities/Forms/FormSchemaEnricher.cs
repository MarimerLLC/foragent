using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Foragent.Capabilities.Forms;

/// <summary>
/// Runs one LLM turn over a deterministic <see cref="FormSchema"/> to infer
/// field dependencies and add a short human-readable note. Fail-soft: any
/// exception or unparseable output leaves the schema unchanged.
///
/// The deterministic mapper (<see cref="FormSchemaMapper"/>) owns all
/// structural fields — type, selector, required, options, validation
/// attributes. The enricher can only add <see cref="FormField.DependsOn"/> and
/// set <see cref="FormSchema.Notes"/>. This division keeps the LLM unable to
/// invent fields or change how the batch capability reaches them.
/// </summary>
public sealed class FormSchemaEnricher(
    IChatClient chatClient,
    ILogger<FormSchemaEnricher> logger)
{
    private const string SystemPrompt = """
        You are reviewing a deterministic schema just extracted from a web form.

        Your job is narrow:
          - Identify any dependency relationships between fields — e.g. the options of a 'state' select are populated only after a 'country' select picks a value.
          - Write one short note about the form a future planner would value (no more than 40 words).

        STRICT RULES:
          - Do NOT add, remove, or rename fields.
          - Do NOT change field types, selectors, required flags, or option lists.
          - Do NOT fabricate dependencies that are not suggested by field names, labels, or the existence of multiple selects in the same form.
          - Use EXACT field names from the input when emitting dependsOn.

        Respond with valid JSON only, no prose, no code fence:
        {
          "notes": "short sentence or empty string",
          "dependsOn": {
            "fieldA": ["fieldB"],
            "fieldC": ["fieldD", "fieldE"]
          }
        }
        Omit fields from dependsOn when there is no dependency. An empty object is fine.
        """;

    public async Task<FormSchema> EnrichAsync(FormSchema schema, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(schema);

        // Pure-text forms with no selects/radios rarely have inter-field
        // dependencies; skip the LLM turn to save tokens.
        if (!schema.Fields.Any(f => f.Type is FormFieldType.Select or FormFieldType.Radio))
            return schema;

        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, SystemPrompt),
                new(ChatRole.User, BuildUserPrompt(schema))
            };
            // No tools — pure summarisation turn.
            var response = await chatClient.GetResponseAsync(messages, new ChatOptions { Tools = [] }, ct);
            var text = response.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
                return schema;

            var json = ExtractJsonObject(text);
            if (json is null)
                return schema;

            using var doc = JsonDocument.Parse(json);
            return Merge(schema, doc.RootElement);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Form-schema enrichment failed; using deterministic schema as-is.");
            return schema;
        }
    }

    private static string BuildUserPrompt(FormSchema schema)
    {
        // Feed only the shape the enricher is allowed to touch: names, types,
        // labels, options. Selectors and URLs are noise for this turn.
        var trimmed = new
        {
            url = schema.Url,
            fields = schema.Fields.Select(f => new
            {
                name = f.Name,
                type = f.Type.ToString(),
                label = f.Label,
                required = f.Required,
                options = f.Options?.Select(o => o.Label ?? o.Value).ToArray()
            }).ToArray()
        };
        return JsonSerializer.Serialize(trimmed, FormSchema.SerializerOptions);
    }

    private static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;
        return text[start..(end + 1)];
    }

    private static FormSchema Merge(FormSchema schema, JsonElement root)
    {
        string? notes = schema.Notes;
        if (root.TryGetProperty("notes", out var notesElement) &&
            notesElement.ValueKind == JsonValueKind.String)
        {
            var raw = notesElement.GetString();
            if (!string.IsNullOrWhiteSpace(raw))
                notes = raw.Trim();
        }

        var dependencies = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (root.TryGetProperty("dependsOn", out var dependsRoot) &&
            dependsRoot.ValueKind == JsonValueKind.Object)
        {
            var fieldNames = schema.Fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var entry in dependsRoot.EnumerateObject())
            {
                if (!fieldNames.Contains(entry.Name))
                    continue;
                if (entry.Value.ValueKind != JsonValueKind.Array)
                    continue;
                var upstream = entry.Value.EnumerateArray()
                    .Where(v => v.ValueKind == JsonValueKind.String)
                    .Select(v => v.GetString()!)
                    .Where(n => fieldNames.Contains(n) && n != entry.Name)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (upstream.Length > 0)
                    dependencies[entry.Name] = upstream;
            }
        }

        if (notes == schema.Notes && dependencies.Count == 0)
            return schema;

        var mergedFields = schema.Fields
            .Select(f => dependencies.TryGetValue(f.Name, out var deps)
                ? f with { DependsOn = deps }
                : f)
            .ToArray();

        return schema with { Fields = mergedFields, Notes = notes };
    }
}
