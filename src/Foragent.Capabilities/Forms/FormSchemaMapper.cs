using Foragent.Browser;

namespace Foragent.Capabilities.Forms;

/// <summary>
/// Pure mapping from the browser-layer <see cref="FormScan"/> (raw HTML
/// attributes) to the wire-level <see cref="FormSchema"/> (typed fields the
/// capability contract exposes). No LLM, no I/O — the floor that
/// <c>learn-form-schema</c> can always produce even when enrichment fails.
/// </summary>
internal static class FormSchemaMapper
{
    public static FormSchema Map(FormScan scan)
    {
        ArgumentNullException.ThrowIfNull(scan);

        var fields = new List<FormField>(scan.Fields.Count);
        var used = new HashSet<string>(StringComparer.Ordinal);
        int unnamedIndex = 0;

        foreach (var raw in scan.Fields)
        {
            if (raw.Selector is null)
                continue; // no deterministic way to reach the field; skip rather than invent

            var name = raw.Name;
            if (string.IsNullOrEmpty(name))
                name = raw.Id;
            if (string.IsNullOrEmpty(name))
                name = $"field{++unnamedIndex}";
            name = Deduplicate(name!, used);

            var type = InferType(raw);
            IReadOnlyList<FormFieldOption>? options = null;
            if (raw.Options is not null && raw.Options.Count > 0)
                options = raw.Options.Select(o => new FormFieldOption(o.Value, o.Label)).ToArray();

            fields.Add(new FormField(
                Name: name,
                Type: type,
                Selector: raw.Selector,
                Label: Normalize(raw.Label),
                Required: raw.Required,
                Options: options,
                Pattern: raw.Pattern,
                Min: raw.Min,
                Max: raw.Max,
                MaxLength: raw.MaxLength,
                DependsOn: null));
        }

        return new FormSchema(
            Version: FormSchema.CurrentVersion,
            Url: scan.Url.ToString(),
            FormSelector: scan.FormSelector,
            SubmitSelector: scan.SubmitSelector,
            SuccessIndicator: null,
            Fields: fields,
            Notes: null);
    }

    private static string Deduplicate(string name, HashSet<string> used)
    {
        if (used.Add(name))
            return name;
        for (var i = 2; ; i++)
        {
            var candidate = $"{name}_{i}";
            if (used.Add(candidate))
                return candidate;
        }
    }

    private static FormFieldType InferType(FormScanField raw) =>
        raw.Tag switch
        {
            "textarea" => FormFieldType.TextArea,
            "select" => FormFieldType.Select,
            "input" => raw.InputType switch
            {
                "email" => FormFieldType.Email,
                "password" => FormFieldType.Password,
                "number" => FormFieldType.Number,
                "date" => FormFieldType.Date,
                "datetime-local" or "datetime" => FormFieldType.DateTime,
                "time" => FormFieldType.Time,
                "url" => FormFieldType.Url,
                "tel" => FormFieldType.Tel,
                "search" => FormFieldType.Search,
                "color" => FormFieldType.Color,
                "hidden" => FormFieldType.Hidden,
                "checkbox" => FormFieldType.Checkbox,
                "radio" => FormFieldType.Radio,
                _ => FormFieldType.Text
            },
            _ => FormFieldType.Text
        };

    private static string? Normalize(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return null;
        var trimmed = label.Trim();
        // Labels often pick up trailing punctuation or whitespace-heavy markup.
        while (trimmed.EndsWith(':') || trimmed.EndsWith('*'))
            trimmed = trimmed[..^1].TrimEnd();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}
