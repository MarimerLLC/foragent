using System.Text.Json;
using Foragent.Browser;
using Foragent.Capabilities.Forms;
using Foragent.Credentials;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.A2A;
using RockBot.Host;
using Xunit;

namespace Foragent.Agent.Tests.Forms;

public class LearnFormSchemaCapabilityTests
{
    [Fact]
    public async Task RejectsInput_WhenUrlMissing()
    {
        var (cap, _, _) = Build();
        var (ctx, _) = TestContext.Build();

        var result = await cap.ExecuteAsync(
            TestContext.Request("learn-form-schema", """{"allowedHosts":["example.com"]}"""),
            ctx);

        Assert.Contains("url", TestContext.TextOf(result));
    }

    [Fact]
    public async Task RejectsInput_WhenAllowlistMissing()
    {
        var (cap, _, _) = Build();
        var (ctx, _) = TestContext.Build();

        var result = await cap.ExecuteAsync(
            TestContext.Request("learn-form-schema", """{"url":"https://example.com/form"}"""),
            ctx);

        Assert.Contains("allowedHosts", TestContext.TextOf(result));
    }

    [Fact]
    public async Task RejectsInput_WhenUrlOffAllowlist()
    {
        var (cap, _, _) = Build();
        var (ctx, _) = TestContext.Build();

        var result = await cap.ExecuteAsync(
            TestContext.Request("learn-form-schema",
                """{"url":"https://evil.example/","allowedHosts":["example.com"]}"""),
            ctx);

        Assert.Contains("not in the allowlist", TestContext.TextOf(result));
    }

    [Fact]
    public async Task ReturnsSchema_AndPersistsSkillWithResource()
    {
        var scan = SampleScan();
        var (cap, factory, skills) = Build();
        var page = new StubBrowserPage { FormScan = scan };
        factory.PageResponder = (_, _) => Task.FromResult<IBrowserPage>(page);
        var (ctx, _) = TestContext.Build();

        var result = await cap.ExecuteAsync(
            TestContext.Request("learn-form-schema",
                """{"url":"https://example.com/contact","allowedHosts":["example.com"]}"""),
            ctx);

        Assert.Equal(AgentTaskState.Completed, result.State);
        using var doc = JsonDocument.Parse(TestContext.TextOf(result));
        Assert.Equal("done", doc.RootElement.GetProperty("status").GetString());
        var skillName = doc.RootElement.GetProperty("skillName").GetString()!;
        Assert.StartsWith("sites/example-com/forms/", skillName);

        var saved = skills.Saved[skillName];
        Assert.NotNull(saved.Manifest);
        Assert.Contains(saved.Manifest!, r => r.Filename == "schema.json" && r.Type == SkillResourceType.JsonSchema);

        // Resource content round-trips as a FormSchema.
        var resourceJson = skills.Resources[skillName]["schema.json"];
        var roundtrip = FormSchema.Deserialize(resourceJson);
        Assert.Equal(2, roundtrip.Fields.Count);
        Assert.Contains(roundtrip.Fields, f => f.Name == "email" && f.Type == FormFieldType.Email && f.Required);
        Assert.Contains(roundtrip.Fields, f => f.Name == "message" && f.Type == FormFieldType.TextArea);
    }

    [Fact]
    public async Task ReturnsError_WhenScanFindsNoForm()
    {
        var (cap, factory, _) = Build();
        var page = new StubBrowserPage { FormScan = null };
        factory.PageResponder = (_, _) => Task.FromResult<IBrowserPage>(page);
        var (ctx, _) = TestContext.Build();

        var result = await cap.ExecuteAsync(
            TestContext.Request("learn-form-schema",
                """{"url":"https://example.com/noform","allowedHosts":["example.com"]}"""),
            ctx);

        Assert.Contains("No form", TestContext.TextOf(result));
    }

    [Fact]
    public async Task MissingCredential_IsReportedWithoutLeakingId()
    {
        var (cap, _, _) = Build();
        var (ctx, _) = TestContext.Build();

        var result = await cap.ExecuteAsync(
            TestContext.Request("learn-form-schema",
                """{"url":"https://example.com/","allowedHosts":["example.com"],"credentialId":"not-there"}"""),
            ctx);

        Assert.Contains("not-there", TestContext.TextOf(result));
        Assert.Contains("not configured", TestContext.TextOf(result));
    }

    private static FormScan SampleScan() => new(
        Url: new Uri("https://example.com/contact"),
        FormSelector: "#contact-form",
        SubmitSelector: "#submit",
        Fields:
        [
            new FormScanField(
                Tag: "input", InputType: "email", Name: "email", Id: "email-input",
                Label: "Email", Required: true, Pattern: null, Min: null, Max: null,
                MaxLength: 120, Options: null, Selector: "input[name=\"email\"]"),
            new FormScanField(
                Tag: "textarea", InputType: null, Name: "message", Id: "msg",
                Label: "Message:", Required: false, Pattern: null, Min: null, Max: null,
                MaxLength: 1000, Options: null, Selector: "textarea[name=\"message\"]")
        ]);

    private static (LearnFormSchemaCapability Capability, StubBrowserSessionFactory Factory, FakeSkillStore Skills) Build()
    {
        var factory = new StubBrowserSessionFactory();
        var skills = new FakeSkillStore();
        // Enricher doesn't run for forms without Select/Radio fields, so an
        // always-empty chat client is safe; it would only be reached for
        // sample forms that include a select.
        var chat = new StubChatClient((_, _) => Task.FromResult(new ChatResponse([])));
        var enricher = new FormSchemaEnricher(chat, NullLogger<FormSchemaEnricher>.Instance);
        var broker = new ThrowingCredentialBroker();
        var cap = new LearnFormSchemaCapability(
            factory,
            broker,
            enricher,
            skills,
            NullLogger<LearnFormSchemaCapability>.Instance);
        return (cap, factory, skills);
    }

    private sealed class ThrowingCredentialBroker : ICredentialBroker
    {
        public Task<CredentialReference> ResolveAsync(string credentialId, CancellationToken cancellationToken = default) =>
            throw new CredentialNotFoundException(credentialId);
    }
}
