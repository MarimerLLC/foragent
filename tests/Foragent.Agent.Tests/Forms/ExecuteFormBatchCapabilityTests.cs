using System.Text.Json;
using Foragent.Browser;
using Foragent.Capabilities.Forms;
using Foragent.Credentials;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.A2A;
using RockBot.Host;
using Xunit;

namespace Foragent.Agent.Tests.Forms;

public class ExecuteFormBatchCapabilityTests
{
    [Fact]
    public async Task RejectsInput_WhenBothSchemaAndSchemaRefAreMissing()
    {
        var (cap, _, _) = Build();
        var (ctx, _) = TestContext.Build();

        var result = await cap.ExecuteAsync(
            TestContext.Request("execute-form-batch",
                """{"rows":[{"email":"a"}],"allowedHosts":["example.com"]}"""),
            ctx);

        Assert.Contains("schemaRef", TestContext.TextOf(result));
    }

    [Fact]
    public async Task RejectsInput_WhenRowsMissing()
    {
        var (cap, _, _) = Build();
        var (ctx, _) = TestContext.Build();

        var result = await cap.ExecuteAsync(
            TestContext.Request("execute-form-batch",
                """{"schemaRef":"sites/example.com/forms/x","allowedHosts":["example.com"]}"""),
            ctx);

        Assert.Contains("rows", TestContext.TextOf(result));
    }

    [Fact]
    public async Task AcceptsStructuredInput_OnDataPart()
    {
        var (cap, factory, skills) = Build();
        await SeedSchemaAsync(skills, "sites/example.com/forms/contact", SampleSchema());
        var page = SubmittingPage();
        factory.PageResponder = (_, _) =>
        {
            page.CurrentUrl = new Uri("https://example.com/form");
            return Task.FromResult<IBrowserPage>(page);
        };

        var (ctx, _) = TestContext.Build();

        var result = await cap.ExecuteAsync(
            TestContext.RequestWithData(
                "execute-form-batch",
                dataJson: """
                {
                  "schemaRef": "sites/example.com/forms/contact",
                  "allowedHosts": ["example.com"],
                  "rows": [
                    {"email":"a@example.com","message":"hi"}
                  ]
                }
                """,
                text: "Submit one contact form row."),
            ctx);

        using var doc = JsonDocument.Parse(TestContext.TextOf(result));
        Assert.Equal("done", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("successCount").GetInt32());
    }

    [Fact]
    public async Task ResolvesSchemaFromRef_AndSubmitsRows()
    {
        var (cap, factory, skills) = Build();
        await SeedSchemaAsync(skills, "sites/example.com/forms/contact", SampleSchema());
        var page = SubmittingPage();
        factory.PageResponder = (_, _) =>
        {
            // Each row opens a fresh page — reset the URL so the URL-change
            // heuristic fires per row rather than only on the first.
            page.CurrentUrl = new Uri("https://example.com/form");
            return Task.FromResult<IBrowserPage>(page);
        };

        var (ctx, capture) = TestContext.Build();

        var result = await cap.ExecuteAsync(
            TestContext.Request("execute-form-batch",
                """
                {
                  "schemaRef": "sites/example.com/forms/contact",
                  "allowedHosts": ["example.com"],
                  "rows": [
                    {"email":"a@example.com","message":"hi"},
                    {"email":"b@example.com","message":"hello"}
                  ]
                }
                """),
            ctx);

        using var doc = JsonDocument.Parse(TestContext.TextOf(result));
        Assert.Equal("done", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("successCount").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("failureCount").GetInt32());
        // One progress update per row (at minimum).
        Assert.True(capture.Statuses.Count >= 2);
    }

    [Fact]
    public async Task AbortOnFirst_StopsAtFailedRow()
    {
        var (cap, factory, _) = Build();
        var page = new StubBrowserPage
        {
            CurrentUrl = new Uri("https://example.com/form")
            // no OnClick → URL never changes → rows fail (URL-change fallback)
        };
        factory.PageResponder = (_, _) => Task.FromResult<IBrowserPage>(page);

        var inline = SampleSchema();
        var inlineJson = JsonSerializer.Serialize(inline, FormSchema.SerializerOptions);
        var (ctx, _) = TestContext.Build();

        var result = await cap.ExecuteAsync(
            TestContext.Request("execute-form-batch",
                $$"""
                {
                  "schema": {{inlineJson}},
                  "allowedHosts": ["example.com"],
                  "rows": [
                    {"email":"a@example.com","message":"1"},
                    {"email":"b@example.com","message":"2"},
                    {"email":"c@example.com","message":"3"}
                  ]
                }
                """),
            ctx);

        using var doc = JsonDocument.Parse(TestContext.TextOf(result));
        Assert.Equal("failed", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("rows").GetArrayLength());
        Assert.Equal(2, doc.RootElement.GetProperty("pending").GetInt32());
    }

    [Fact]
    public async Task ContinueMode_RunsAllRowsEvenWhenSomeFail()
    {
        var (cap, factory, _) = Build();
        // First row fails (URL unchanged, no OnClick); then OnClick toggles so
        // subsequent rows succeed via URL change.
        var attempts = 0;
        var page = new StubBrowserPage { CurrentUrl = new Uri("https://example.com/form") };
        page.OnClick = _ =>
        {
            attempts++;
            if (attempts >= 2)
                page.CurrentUrl = new Uri($"https://example.com/thanks/{attempts}");
        };
        factory.PageResponder = (_, _) =>
        {
            // Fresh session page per row — reset URL.
            page.CurrentUrl = new Uri("https://example.com/form");
            return Task.FromResult<IBrowserPage>(page);
        };

        var inline = SampleSchema();
        var inlineJson = JsonSerializer.Serialize(inline, FormSchema.SerializerOptions);
        var (ctx, _) = TestContext.Build();

        var result = await cap.ExecuteAsync(
            TestContext.Request("execute-form-batch",
                $$"""
                {
                  "schema": {{inlineJson}},
                  "allowedHosts": ["example.com"],
                  "mode": "continue",
                  "rows": [
                    {"email":"a@example.com","message":"1"},
                    {"email":"b@example.com","message":"2"},
                    {"email":"c@example.com","message":"3"}
                  ]
                }
                """),
            ctx);

        using var doc = JsonDocument.Parse(TestContext.TextOf(result));
        Assert.Equal("partial", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("rows").GetArrayLength());
        Assert.Equal(0, doc.RootElement.GetProperty("pending").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("failureCount").GetInt32());
    }

    [Fact]
    public async Task ValidationFails_WhenRequiredFieldMissing()
    {
        var (cap, factory, _) = Build();
        var page = SubmittingPage();
        factory.PageResponder = (_, _) => Task.FromResult<IBrowserPage>(page);

        var inline = SampleSchema();
        var inlineJson = JsonSerializer.Serialize(inline, FormSchema.SerializerOptions);
        var (ctx, _) = TestContext.Build();

        var result = await cap.ExecuteAsync(
            TestContext.Request("execute-form-batch",
                $$"""
                {
                  "schema": {{inlineJson}},
                  "allowedHosts": ["example.com"],
                  "rows": [ {"message":"missing email"} ]
                }
                """),
            ctx);

        using var doc = JsonDocument.Parse(TestContext.TextOf(result));
        var firstRow = doc.RootElement.GetProperty("rows")[0];
        Assert.Equal("validation-error", firstRow.GetProperty("status").GetString());
        Assert.Contains("email", firstRow.GetProperty("error").GetString()!);
        // Validation should have short-circuited before any navigation.
        Assert.DoesNotContain(page.Actions, a => a.StartsWith("navigate:"));
    }

    [Fact]
    public async Task UsesSuccessIndicator_WhenProvided()
    {
        var (cap, factory, _) = Build();
        var page = new StubBrowserPage { CurrentUrl = new Uri("https://example.com/form") };
        page.OnClick = _ => { /* don't change URL; rely on successIndicator */ };
        factory.PageResponder = (_, _) => Task.FromResult<IBrowserPage>(page);

        var inline = SampleSchema();
        var inlineJson = JsonSerializer.Serialize(inline, FormSchema.SerializerOptions);
        var (ctx, _) = TestContext.Build();

        var result = await cap.ExecuteAsync(
            TestContext.Request("execute-form-batch",
                $$"""
                {
                  "schema": {{inlineJson}},
                  "allowedHosts": ["example.com"],
                  "successIndicator": ".thanks",
                  "rows": [ {"email":"a@example.com","message":"hi"} ]
                }
                """),
            ctx);

        using var doc = JsonDocument.Parse(TestContext.TextOf(result));
        Assert.Equal("done", doc.RootElement.GetProperty("status").GetString());
        Assert.Contains("wait:.thanks", page.Actions);
    }

    [Fact]
    public async Task MissingSchemaResource_ReportsError()
    {
        var (cap, _, _) = Build();
        var (ctx, _) = TestContext.Build();

        var result = await cap.ExecuteAsync(
            TestContext.Request("execute-form-batch",
                """
                {
                  "schemaRef": "sites/example.com/forms/absent",
                  "allowedHosts": ["example.com"],
                  "rows": [ {"email":"a@example.com"} ]
                }
                """),
            ctx);

        Assert.Contains("No schema.json resource", TestContext.TextOf(result));
    }

    private static async Task SeedSchemaAsync(FakeSkillStore store, string name, FormSchema schema)
    {
        var skill = new Skill(
            Name: name,
            Summary: "test",
            Content: "seeded",
            CreatedAt: DateTimeOffset.UtcNow);
        var resource = new SkillResourceInput(
            Filename: "schema.json",
            Type: SkillResourceType.JsonSchema,
            Description: "test",
            Content: schema.Serialize());
        await store.SaveAsync(skill, [resource]);
    }

    private static FormSchema SampleSchema() => new(
        Version: FormSchema.CurrentVersion,
        Url: "https://example.com/form",
        FormSelector: "#contact",
        SubmitSelector: "#submit",
        SuccessIndicator: null,
        Fields:
        [
            new FormField(
                Name: "email",
                Type: FormFieldType.Email,
                Selector: "input[name=\"email\"]",
                Label: "Email",
                Required: true),
            new FormField(
                Name: "message",
                Type: FormFieldType.TextArea,
                Selector: "textarea[name=\"message\"]",
                Label: "Message",
                Required: false)
        ],
        Notes: null);

    private static StubBrowserPage SubmittingPage()
    {
        var page = new StubBrowserPage { CurrentUrl = new Uri("https://example.com/form") };
        page.OnClick = sel =>
        {
            if (sel == "#submit")
                page.CurrentUrl = new Uri("https://example.com/thanks");
        };
        return page;
    }

    private static (ExecuteFormBatchCapability Capability, StubBrowserSessionFactory Factory, FakeSkillStore Skills) Build()
    {
        var factory = new StubBrowserSessionFactory();
        var skills = new FakeSkillStore();
        var broker = new ThrowingCredentialBroker();
        var cap = new ExecuteFormBatchCapability(
            factory,
            broker,
            skills,
            NullLogger<ExecuteFormBatchCapability>.Instance);
        return (cap, factory, skills);
    }

    private sealed class ThrowingCredentialBroker : ICredentialBroker
    {
        public Task<CredentialReference> ResolveAsync(string credentialId, CancellationToken cancellationToken = default) =>
            throw new CredentialNotFoundException(credentialId);
    }
}
