using System.Text.Json;
using Foragent.Browser;
using Foragent.Credentials;
using Microsoft.Extensions.Logging;
using RockBot.A2A;
using RockBot.Host;

namespace Foragent.Capabilities.Forms;

/// <summary>
/// Phase-3 of the learn → review → execute flow (spec §5.5). Submits a batch
/// of rows against a previously-learned <see cref="FormSchema"/>, either
/// inline or fetched from <see cref="ISkillStore"/> by name. Streams per-row
/// <see cref="AgentTaskStatusUpdate"/>s while running.
///
/// Default mode is <c>abort-on-first</c> (spec open-question #8 resolution):
/// a row failure halts the batch, since continuing past a failure on a
/// mutating form typically generates bad data rather than surfacing the
/// real problem. Callers opt into <c>continue</c> when row-level data
/// quality is known-messy and partial success is desired.
/// </summary>
public sealed class ExecuteFormBatchCapability(
    IBrowserSessionFactory browserFactory,
    ICredentialBroker credentialBroker,
    ISkillStore skillStore,
    ILogger<ExecuteFormBatchCapability> logger) : ICapability
{
    public static AgentSkill SkillDefinition { get; } = new()
    {
        Id = "execute-form-batch",
        Name = "Execute Form Batch",
        Description = "Submit a batch of rows against a learned form schema. "
            + "PASS INPUT AS AN A2A DATA PART (a structured JSON object), not as prose inside the text message. "
            + "When calling via RockBot's invoke_agent, populate the 'data' parameter with this object — the multi-row shape doesn't fit in plain text. "
            + "Fields: {\"schemaRef\":\"sites/host/forms/name\" OR \"schema\":{...FormSchema...},\"rows\":[{fieldName:value,...}],\"allowedHosts\":[\"host\"],\"credentialId\":\"optional\",\"mode\":\"abort-on-first\"|\"continue\",\"successIndicator\":\"optional CSS selector\"}. "
            + "'rows', 'allowedHosts', and exactly one of 'schemaRef'/'schema' are REQUIRED. "
            + "Streams per-row progress. Default mode aborts on first failure."
    };

    public string SkillId => SkillDefinition.Id;
    public AgentSkill Skill => SkillDefinition;

    public async Task<AgentTaskResult> ExecuteAsync(AgentTaskRequest request, AgentTaskContext context)
    {
        var ct = context.MessageContext.CancellationToken;
        var input = ExecuteFormBatchInput.Parse(request);
        if (input.Error is not null)
            return CapabilityResult.Error(request, input.Error);

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
            schema = await ResolveSchemaAsync(input);
        }
        catch (Exception ex)
        {
            return CapabilityResult.Error(request, ex.Message);
        }

        if (!Uri.TryCreate(schema.Url, UriKind.Absolute, out var formUrl))
            return CapabilityResult.Error(request, $"Schema URL '{schema.Url}' is not a valid absolute URL.");
        if (!input.Allowlist!.IsAllowed(formUrl))
            return CapabilityResult.Error(request, $"Schema URL host '{formUrl.Host}' is not in the allowlist.");
        if (string.IsNullOrWhiteSpace(schema.SubmitSelector))
            return CapabilityResult.Error(request, "Schema does not specify a submit selector — cannot submit without it.");

        var results = new List<RowResult>(input.Rows!.Count);
        var submittedAny = false;

        try
        {
            await using var session = await browserFactory.CreateSessionAsync(input.Allowlist.IsAllowed, ct);

            for (var i = 0; i < input.Rows.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var row = input.Rows[i];

                await PublishProgressAsync(context, request, $"Submitting row {i + 1} of {input.Rows.Count}...", ct);

                var rowResult = await SubmitRowAsync(session, schema, row, input.SuccessIndicator, i, ct);
                results.Add(rowResult);
                if (rowResult.Status == RowStatus.Success)
                    submittedAny = true;

                if (rowResult.Status != RowStatus.Success && input.Mode == ExecuteFormBatchMode.AbortOnFirst)
                {
                    logger.LogInformation(
                        "Aborting batch after row {Index} failure: {Reason}", i, rowResult.Error);
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "execute-form-batch failed outside the row loop after {Count} row(s)", results.Count);
            return CapabilityResult.Error(request, $"Batch failed: {ex.Message}");
        }

        var successCount = results.Count(r => r.Status == RowStatus.Success);
        var failureCount = results.Count(r => r.Status != RowStatus.Success);
        var pending = input.Rows.Count - results.Count;

        string status;
        if (failureCount == 0 && pending == 0)
            status = "done";
        else if (submittedAny)
            status = "partial";
        else
            status = "failed";

        var payload = new
        {
            status,
            mode = input.Mode == ExecuteFormBatchMode.Continue ? "continue" : "abort-on-first",
            successCount,
            failureCount,
            pending,
            rows = results.Select(r => new
            {
                index = r.Index,
                status = r.Status switch
                {
                    RowStatus.Success => "success",
                    RowStatus.ValidationError => "validation-error",
                    _ => "failed"
                },
                error = r.Error
            }).ToArray()
        };
        return CapabilityResult.Completed(
            request,
            JsonSerializer.Serialize(payload, FormSchema.SerializerOptions));
    }

    private async Task<FormSchema> ResolveSchemaAsync(ExecuteFormBatchInput input)
    {
        if (input.InlineSchema is not null)
            return input.InlineSchema;

        var content = await skillStore.GetResourceAsync(input.SchemaRef!, "schema.json");
        if (content is null)
            throw new InvalidOperationException(
                $"No schema.json resource found for skill '{input.SchemaRef}'. Run learn-form-schema first.");

        try
        {
            return FormSchema.Deserialize(content);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Skill '{input.SchemaRef}' has a schema.json resource that isn't a valid FormSchema: {ex.Message}");
        }
    }

    private async Task<RowResult> SubmitRowAsync(
        IBrowserSession session,
        FormSchema schema,
        IReadOnlyDictionary<string, string> row,
        string? externalSuccessIndicator,
        int index,
        CancellationToken ct)
    {
        // Pre-validate required fields so we fail before spending a network
        // round-trip on a row that could never succeed.
        var missing = schema.Fields
            .Where(f => f.Required && f.Type != FormFieldType.Hidden)
            .Where(f => !row.TryGetValue(f.Name, out var v) || string.IsNullOrEmpty(v))
            .Select(f => f.Name)
            .ToList();
        if (missing.Count > 0)
        {
            return new RowResult(index, RowStatus.ValidationError,
                $"Missing required field(s): {string.Join(", ", missing)}.");
        }

        var formUri = new Uri(schema.Url);
        IBrowserPage? page = null;
        try
        {
            page = await session.OpenPageAsync(formUri, ct);
            var formSelector = string.IsNullOrWhiteSpace(schema.FormSelector) ? "form" : schema.FormSelector!;
            try
            {
                await page.WaitForSelectorAsync(formSelector, TimeSpan.FromSeconds(15), ct);
            }
            catch (TimeoutException)
            {
                return new RowResult(index, RowStatus.Failed, $"Form '{formSelector}' did not appear within 15s.");
            }

            foreach (var field in schema.Fields)
            {
                if (field.Type == FormFieldType.Hidden)
                    continue;
                if (!row.TryGetValue(field.Name, out var value))
                    continue;

                try
                {
                    await SetFieldAsync(page, field, value, ct);
                }
                catch (Exception ex)
                {
                    return new RowResult(index, RowStatus.Failed,
                        $"Failed to fill '{field.Name}': {ex.Message}");
                }
            }

            var urlBefore = await page.GetUrlAsync(ct);
            try
            {
                await page.ClickAsync(schema.SubmitSelector!, ct);
            }
            catch (Exception ex)
            {
                return new RowResult(index, RowStatus.Failed,
                    $"Failed to click submit '{schema.SubmitSelector}': {ex.Message}");
            }

            var indicator = externalSuccessIndicator ?? schema.SuccessIndicator;
            if (!string.IsNullOrWhiteSpace(indicator))
            {
                try
                {
                    await page.WaitForSelectorAsync(indicator, TimeSpan.FromSeconds(15), ct);
                    return new RowResult(index, RowStatus.Success, null);
                }
                catch (TimeoutException)
                {
                    return new RowResult(index, RowStatus.Failed,
                        $"Success indicator '{indicator}' did not appear within 15s.");
                }
            }

            // No explicit success signal — fall back to URL change as a weak
            // heuristic. Forms that submit in place without navigation will
            // need a successIndicator; the error mentions it.
            await Task.Delay(500, ct);
            var urlAfter = await page.GetUrlAsync(ct);
            if (urlAfter != urlBefore)
                return new RowResult(index, RowStatus.Success, null);

            return new RowResult(index, RowStatus.Failed,
                "URL did not change after submit and no successIndicator was provided; cannot confirm success.");
        }
        finally
        {
            if (page is not null)
                await page.DisposeAsync();
        }
    }

    private static async Task SetFieldAsync(
        IBrowserPage page,
        FormField field,
        string value,
        CancellationToken ct)
    {
        switch (field.Type)
        {
            case FormFieldType.Select:
                await page.SelectOptionAsync(field.Selector, value, ct);
                break;
            case FormFieldType.Checkbox:
                await page.SetCheckedAsync(field.Selector, ParseBool(value), ct);
                break;
            case FormFieldType.Radio:
                // The schema's radio selector matches the whole group; append
                // a value attribute to target the specific option.
                await page.SetCheckedAsync(
                    $"{field.Selector}[value={JsonSerializer.Serialize(value)}]",
                    true,
                    ct);
                break;
            default:
                await page.FillAsync(field.Selector, value, ct);
                break;
        }
    }

    private static bool ParseBool(string value) =>
        value.Equals("true", StringComparison.OrdinalIgnoreCase)
        || value == "1"
        || value.Equals("on", StringComparison.OrdinalIgnoreCase)
        || value.Equals("yes", StringComparison.OrdinalIgnoreCase);

    private static Task PublishProgressAsync(
        AgentTaskContext context, AgentTaskRequest request, string message, CancellationToken ct) =>
        context.PublishStatus(new AgentTaskStatusUpdate
        {
            TaskId = request.TaskId,
            ContextId = request.ContextId,
            State = AgentTaskState.Working,
            Message = new AgentMessage
            {
                Role = "agent",
                Parts = [new AgentMessagePart { Kind = "text", Text = message }]
            }
        }, ct);

    private enum RowStatus
    {
        Success,
        ValidationError,
        Failed
    }

    private sealed record RowResult(int Index, RowStatus Status, string? Error);
}
