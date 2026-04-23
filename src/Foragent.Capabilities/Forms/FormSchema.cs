using System.Text.Json;
using System.Text.Json.Serialization;

namespace Foragent.Capabilities.Forms;

/// <summary>
/// Typed schema for a web form produced by <c>learn-form-schema</c> (spec §5.2)
/// and consumed by <c>execute-form-batch</c>. Persisted as a
/// <see cref="RockBot.Host.SkillResourceType.JsonSchema"/> resource attached to
/// a <see cref="RockBot.Host.Skill"/> named <c>sites/{host}/forms/{slug}</c>.
///
/// This is the on-the-wire shape — callers exchange it as JSON and reference
/// it by skill name, not by process-local object identity.
/// </summary>
public sealed record FormSchema(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("formSelector")] string? FormSelector,
    [property: JsonPropertyName("submitSelector")] string? SubmitSelector,
    [property: JsonPropertyName("successIndicator")] string? SuccessIndicator,
    [property: JsonPropertyName("fields")] IReadOnlyList<FormField> Fields,
    [property: JsonPropertyName("notes")] string? Notes = null)
{
    public const int CurrentVersion = 1;

    /// <summary>
    /// Stable <see cref="JsonSerializerOptions"/> used for every persisted and
    /// wire-transmitted schema. Never rebuild; the string shape is part of the
    /// skill resource format.
    /// </summary>
    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public string Serialize() => JsonSerializer.Serialize(this, SerializerOptions);

    public static FormSchema Deserialize(string json) =>
        JsonSerializer.Deserialize<FormSchema>(json, SerializerOptions)
            ?? throw new InvalidOperationException("Form schema JSON was null.");
}

/// <summary>
/// One field in a <see cref="FormSchema"/>. <see cref="Selector"/> is a
/// Playwright-dialect CSS selector — the concrete hook <c>execute-form-batch</c>
/// uses to reach the input. <see cref="Name"/> is the JSON key callers put
/// values under when they build rows for a batch.
/// </summary>
public sealed record FormField(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] FormFieldType Type,
    [property: JsonPropertyName("selector")] string Selector,
    [property: JsonPropertyName("label")] string? Label = null,
    [property: JsonPropertyName("required")] bool Required = false,
    [property: JsonPropertyName("options")] IReadOnlyList<FormFieldOption>? Options = null,
    [property: JsonPropertyName("pattern")] string? Pattern = null,
    [property: JsonPropertyName("min")] string? Min = null,
    [property: JsonPropertyName("max")] string? Max = null,
    [property: JsonPropertyName("maxLength")] int? MaxLength = null,
    [property: JsonPropertyName("dependsOn")] IReadOnlyList<string>? DependsOn = null);

/// <summary>
/// An option entry for a <see cref="FormFieldType.Select"/> or
/// <see cref="FormFieldType.Radio"/> field. <see cref="Value"/> is the string a
/// caller supplies in a row payload; <see cref="Label"/> is the human text
/// rendered on the page.
/// </summary>
public sealed record FormFieldOption(
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("label")] string? Label = null);

public enum FormFieldType
{
    Text,
    Email,
    Password,
    Number,
    Date,
    DateTime,
    Time,
    Url,
    Tel,
    Search,
    Color,
    Hidden,
    TextArea,
    Select,
    Radio,
    Checkbox
}
