using System.Text;
using Foragent.Browser;
using Foragent.Credentials;
using Microsoft.Extensions.Logging;
using RockBot.A2A;
using RockBot.Host;

namespace Foragent.Capabilities.Forms;

/// <summary>
/// Phase-1 of the learn → review → execute flow (spec §5.5). Navigates to a
/// form, scans it deterministically, runs one LLM pass to infer dependencies
/// and compose a short note, persists the schema alongside a markdown primer
/// as a <see cref="Skill"/> + <see cref="SkillResourceType.JsonSchema"/>
/// resource, and returns the schema inline for caller review.
///
/// Scope cuts for step 8 (spec §9.1 step 8): file uploads and multi-step
/// wizards are out of scope. A single static form per call.
/// </summary>
public sealed class LearnFormSchemaCapability(
    IBrowserSessionFactory browserFactory,
    ICredentialBroker credentialBroker,
    FormSchemaEnricher enricher,
    ISkillStore skillStore,
    ILogger<LearnFormSchemaCapability> logger) : ICapability
{
    public static AgentSkill SkillDefinition { get; } = new()
    {
        Id = "learn-form-schema",
        Name = "Learn Form Schema",
        Description = "Navigate to a web form, extract its structure (fields, types, options, validation), and persist it as a reusable skill. "
            + "Input: JSON {\"url\":\"https://...\",\"allowedHosts\":[\"host\"],\"formSelector\":\"optional\",\"credentialId\":\"optional\",\"skillName\":\"optional override\",\"intent\":\"optional prose\"}. "
            + "Returns the typed form schema plus the skill name it was persisted under."
    };

    public string SkillId => SkillDefinition.Id;
    public AgentSkill Skill => SkillDefinition;

    public async Task<AgentTaskResult> ExecuteAsync(AgentTaskRequest request, AgentTaskContext context)
    {
        var ct = context.MessageContext.CancellationToken;
        var input = LearnFormSchemaInput.Parse(request);
        if (input.Error is not null)
            return CapabilityResult.Error(request, input.Error);

        // credentialId is accepted for future use (authenticated forms); resolve
        // to fail fast and audit-log access if supplied. Not consumed by the
        // scan itself — the caller should pre-authenticate via browser-task and
        // rely on storage-state (spec §6.5, deferred) to reach the form.
        if (!string.IsNullOrWhiteSpace(input.CredentialId))
        {
            try
            {
                _ = await credentialBroker.ResolveAsync(input.CredentialId!, ct);
            }
            catch (CredentialNotFoundException ex)
            {
                return CapabilityResult.Error(request, $"Credential '{ex.CredentialId}' is not configured.");
            }
        }

        FormSchema schema;
        try
        {
            await using var session = await browserFactory.CreateSessionAsync(input.Allowlist!.IsAllowed, ct);
            await using var page = await session.OpenPageAsync(input.Url!, ct);

            var selector = string.IsNullOrWhiteSpace(input.FormSelector) ? "form" : input.FormSelector!;
            try
            {
                await page.WaitForSelectorAsync(selector, TimeSpan.FromSeconds(15), ct);
            }
            catch (TimeoutException)
            {
                return CapabilityResult.Error(request, $"No form matching '{selector}' appeared within 15s.");
            }

            var scan = await page.ScanFormAsync(input.FormSelector, ct);
            if (scan is null)
                return CapabilityResult.Error(request, $"No form matching '{selector}' was found on {input.Url}.");
            if (scan.Fields.Count == 0)
                return CapabilityResult.Error(request, $"The form at {input.Url} had no recognizable input fields.");

            var deterministic = FormSchemaMapper.Map(scan);
            schema = await enricher.EnrichAsync(deterministic, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "learn-form-schema failed for {Url}", input.Url);
            return CapabilityResult.Error(request, $"Form scan failed: {ex.Message}");
        }

        var skillName = input.SkillName ?? DeriveSkillName(input.Url!, input.Intent);
        try
        {
            await PersistAsync(skillName, schema, input.Intent, ct);
            logger.LogInformation("Persisted form schema '{SkillName}' ({FieldCount} fields).", skillName, schema.Fields.Count);
        }
        catch (Exception ex)
        {
            // Persistence failure shouldn't fail the task — the schema still
            // goes back inline and the caller can retry. Log for the operator.
            logger.LogWarning(ex, "Failed to persist skill '{SkillName}'; returning schema inline anyway.", skillName);
        }

        var payload = new
        {
            status = "done",
            skillName,
            schema
        };
        return CapabilityResult.Completed(
            request,
            System.Text.Json.JsonSerializer.Serialize(payload, FormSchema.SerializerOptions));
    }

    private async Task PersistAsync(string skillName, FormSchema schema, string? intent, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var existing = await skillStore.GetAsync(skillName);
        var now = DateTimeOffset.UtcNow;

        var skill = new Skill(
            Name: skillName,
            Summary: BuildSummary(schema, intent),
            Content: BuildPrimer(schema, intent),
            CreatedAt: existing?.CreatedAt ?? now,
            UpdatedAt: existing is null ? null : now,
            LastUsedAt: now,
            SeeAlso: existing?.SeeAlso,
            Manifest: null);

        var resource = new SkillResourceInput(
            Filename: "schema.json",
            Type: SkillResourceType.JsonSchema,
            Description: "Typed form schema — fields, types, options, validation, dependencies.",
            Content: schema.Serialize());

        await skillStore.SaveAsync(skill, [resource]);
    }

    private static string BuildSummary(FormSchema schema, string? intent)
    {
        var uri = new Uri(schema.Url);
        var host = uri.Host;
        var prefix = string.IsNullOrWhiteSpace(intent) ? $"Form on {host}" : intent!.Trim();
        if (prefix.Length > 120) prefix = prefix[..120];
        return $"{prefix} — {schema.Fields.Count} fields.";
    }

    private static string BuildPrimer(FormSchema schema, string? intent)
    {
        var sb = new StringBuilder();
        sb.Append("# Form: ").AppendLine(schema.Url);
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(intent))
        {
            sb.AppendLine(intent!.Trim());
            sb.AppendLine();
        }
        sb.Append("Form selector: `").Append(schema.FormSelector ?? "form").AppendLine("`");
        if (!string.IsNullOrWhiteSpace(schema.SubmitSelector))
            sb.Append("Submit selector: `").Append(schema.SubmitSelector).AppendLine("`");
        sb.AppendLine();
        sb.AppendLine("## Fields");
        sb.AppendLine();
        foreach (var f in schema.Fields)
        {
            sb.Append("- **").Append(f.Name).Append("** (`").Append(f.Type).Append("`");
            if (f.Required) sb.Append(", required");
            sb.Append(") — selector `").Append(f.Selector).Append('`');
            if (!string.IsNullOrWhiteSpace(f.Label))
                sb.Append(" — label: ").Append(f.Label);
            if (f.Options is { Count: > 0 })
                sb.Append(" — options: ").Append(string.Join(", ", f.Options.Select(o => o.Label ?? o.Value)));
            if (f.DependsOn is { Count: > 0 })
                sb.Append(" — depends on: ").Append(string.Join(", ", f.DependsOn));
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(schema.Notes))
        {
            sb.AppendLine();
            sb.AppendLine("## Notes");
            sb.AppendLine();
            sb.AppendLine(schema.Notes);
        }

        sb.AppendLine();
        sb.AppendLine("The typed schema lives in the `schema.json` resource attached to this skill — that is the file `execute-form-batch` consumes.");
        return sb.ToString();
    }

    private static string DeriveSkillName(Uri url, string? intent)
    {
        var host = url.Host.ToLowerInvariant();
        var slug = string.IsNullOrWhiteSpace(intent)
            ? SlugFromPath(url.AbsolutePath)
            : Slugify(intent!);
        if (string.IsNullOrEmpty(slug))
            slug = "form";
        return $"sites/{host}/forms/{slug}";
    }

    private static string SlugFromPath(string path)
    {
        var trimmed = path.Trim('/');
        if (string.IsNullOrEmpty(trimmed))
            return "root";
        return Slugify(trimmed);
    }

    private static string Slugify(string text)
    {
        var sb = new StringBuilder(capacity: Math.Min(text.Length, 48));
        var lastDash = true;
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastDash = false;
            }
            else if (!lastDash && sb.Length < 48)
            {
                sb.Append('-');
                lastDash = true;
            }
            if (sb.Length >= 48) break;
        }
        return sb.ToString().Trim('-');
    }
}
