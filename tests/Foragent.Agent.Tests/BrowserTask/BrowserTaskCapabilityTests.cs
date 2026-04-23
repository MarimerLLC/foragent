using System.Text.Json;
using Foragent.Capabilities.BrowserTask;
using Foragent.Credentials;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.A2A;
using RockBot.Host;
using Xunit;

namespace Foragent.Agent.Tests.BrowserTask;

public class BrowserTaskCapabilityTests
{
    [Fact]
    public async Task RejectsInput_WhenAllowlistMissing()
    {
        var (capability, _, _) = Build(ScriptedChatClient.Text("unused"));
        var (ctx, _) = TestContext.Build();

        var result = await capability.ExecuteAsync(
            TestContext.Request("browser-task",
                """{"intent":"do stuff"}"""),
            ctx);

        Assert.Equal(AgentTaskState.Completed, result.State);
        Assert.Contains("allowedHosts", TestContext.TextOf(result));
    }

    [Fact]
    public async Task RejectsInput_WhenIntentMissing()
    {
        var (capability, _, _) = Build(ScriptedChatClient.Text("unused"));
        var (ctx, _) = TestContext.Build();

        var result = await capability.ExecuteAsync(
            TestContext.Request("browser-task",
                """{"allowedHosts":["*"]}"""),
            ctx);

        Assert.Contains("intent", TestContext.TextOf(result));
    }

    [Fact]
    public async Task DonePayload_ReturnsStructuredJson()
    {
        var (capability, page, _) = Build(
            ScriptedChatClient.ToolCall("snapshot"),
            ScriptedChatClient.ToolCall("done", new { summary = "found the title", result = "Example Domain" }),
            ScriptedChatClient.Text("stopping"));
        var (ctx, _) = TestContext.Build();

        var result = await capability.ExecuteAsync(
            TestContext.Request("browser-task",
                """{"intent":"read the page title","url":"https://example.com/","allowedHosts":["example.com"]}"""),
            ctx);

        Assert.Equal(AgentTaskState.Completed, result.State);
        var text = TestContext.TextOf(result);
        using var doc = JsonDocument.Parse(text);
        Assert.Equal("done", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("found the title", doc.RootElement.GetProperty("summary").GetString());
        Assert.Equal("Example Domain", doc.RootElement.GetProperty("result").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("steps").GetInt32());
        Assert.Contains("snapshot", page.Actions);
    }

    [Fact]
    public async Task Fail_ReturnsFailedStatus()
    {
        var (capability, _, _) = Build(
            ScriptedChatClient.ToolCall("fail", new { reason = "unreachable" }),
            ScriptedChatClient.Text("stopping"));
        var (ctx, _) = TestContext.Build();

        var result = await capability.ExecuteAsync(
            TestContext.Request("browser-task",
                """{"intent":"try something","allowedHosts":["*"]}"""),
            ctx);

        using var doc = JsonDocument.Parse(TestContext.TextOf(result));
        Assert.Equal("failed", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("unreachable", doc.RootElement.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task BudgetExhausted_ReturnsIncompleteStatus()
    {
        // Script drives the planner to snapshot repeatedly past the budget.
        // maxSteps=2 — after 2 snapshot calls the tool returns a budget
        // message; the model keeps calling snapshot but each call is a
        // budget-exhausted no-op. The script runs out and returns a final
        // text, ending the loop naturally.
        var responses = new List<ChatResponse>();
        for (var i = 0; i < 10; i++)
            responses.Add(ScriptedChatClient.ToolCall("snapshot"));
        responses.Add(ScriptedChatClient.Text("giving up"));

        var (capability, _, _) = Build([.. responses]);
        var (ctx, _) = TestContext.Build();

        var result = await capability.ExecuteAsync(
            TestContext.Request("browser-task",
                """{"intent":"spin forever","allowedHosts":["*"],"maxSteps":2}"""),
            ctx);

        using var doc = JsonDocument.Parse(TestContext.TextOf(result));
        Assert.Equal("incomplete", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task OffAllowlistStartingUrl_IsRejected()
    {
        var (capability, _, _) = Build(ScriptedChatClient.Text("unused"));
        var (ctx, _) = TestContext.Build();

        var result = await capability.ExecuteAsync(
            TestContext.Request("browser-task",
                """{"intent":"go","url":"https://other.example/","allowedHosts":["bsky.app"]}"""),
            ctx);

        Assert.Contains("not in the allowlist", TestContext.TextOf(result));
    }

    [Fact]
    public async Task NavigateTool_RejectsOffAllowlistHosts()
    {
        var (capability, page, _) = Build(
            ScriptedChatClient.ToolCall("navigate", new { url = "https://evil.example/phish" }),
            ScriptedChatClient.ToolCall("done", new { summary = "model gave up" }),
            ScriptedChatClient.Text("stopping"));
        var (ctx, _) = TestContext.Build();

        var result = await capability.ExecuteAsync(
            TestContext.Request("browser-task",
                """{"intent":"try","allowedHosts":["bsky.app"]}"""),
            ctx);

        using var doc = JsonDocument.Parse(TestContext.TextOf(result));
        Assert.Equal("done", doc.RootElement.GetProperty("status").GetString());
        // Fake page.NavigateAsync never ran because the tool rejected first.
        Assert.DoesNotContain(page.Actions, a => a.StartsWith("navigate:"));
    }

    private static (BrowserTaskCapability Capability, FakeBrowserAgentPage Page, FakeAgentBrowserSessionFactory Factory) Build(
        params ChatResponse[] script)
    {
        var (cap, page, factory, _, _, _) = BuildFull(new FakeSkillStore(), new FakeLongTermMemory(), script);
        return (cap, page, factory);
    }

    internal static (
        BrowserTaskCapability Capability,
        FakeBrowserAgentPage Page,
        FakeAgentBrowserSessionFactory Factory,
        ScriptedChatClient Scripted,
        FakeSkillStore Skills,
        FakeLongTermMemory Memory) BuildFull(
            FakeSkillStore skillStore,
            FakeLongTermMemory memory,
            params ChatResponse[] script)
    {
        var page = new FakeBrowserAgentPage();
        var factory = new FakeAgentBrowserSessionFactory(page);

        var scripted = new ScriptedChatClient(script);
        var wrapped = new ChatClientBuilder(scripted)
            .UseFunctionInvocation()
            .Build();

        var broker = new StubCredentialBroker();
        var priming = new BrowserTaskPriming(
            skillStore,
            memory,
            NullLogger<BrowserTaskPriming>.Instance);

        var capability = new BrowserTaskCapability(
            factory,
            wrapped,
            broker,
            priming,
            skillStore,
            NullLogger<BrowserTaskCapability>.Instance);

        return (capability, page, factory, scripted, skillStore, memory);
    }

    // ── Step 7: learning-substrate tests ────────────────────────────────────

    [Fact]
    public async Task Priming_InjectsRetrievedSkillIntoUserPrompt()
    {
        var skills = new FakeSkillStore();
        await skills.SaveAsync(new Skill(
            Name: "sites/example-com/login",
            Summary: "Use the app password, not the account password.",
            Content: "Click 'Sign in', enter handle, then password.",
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: null,
            LastUsedAt: null,
            SeeAlso: []));

        var (capability, _, _, scripted, _, _) = BuildFull(
            skills,
            new FakeLongTermMemory(),
            ScriptedChatClient.ToolCall("done", new { summary = "primed" }),
            ScriptedChatClient.Text("stopping"));
        var (ctx, _) = TestContext.Build();

        await capability.ExecuteAsync(
            TestContext.Request("browser-task",
                """{"intent":"sign in somewhere","allowedHosts":["example.com"]}"""),
            ctx);

        var userMessage = scripted.FirstMessages.Single(m => m.Role == ChatRole.User).Text ?? string.Empty;
        Assert.Contains("Known site knowledge", userMessage);
        Assert.Contains("sites/example-com/login", userMessage);
        Assert.Contains("app password", userMessage);
    }

    [Fact]
    public async Task LearnedSkill_IsWrittenOnSuccess_WhenMultipleNavigations()
    {
        var skills = new FakeSkillStore();

        // Two turns of tool calls drive two navigations, then done() triggers
        // the learned-skill synthesis turn. That synthesis call is the 4th
        // scripted response (summary on line 1, content after a blank line).
        var (capability, _, _, _, _, _) = BuildFull(
            skills,
            new FakeLongTermMemory(),
            ScriptedChatClient.ToolCall("navigate", new { url = "https://example.com/page1" }),
            ScriptedChatClient.ToolCall("navigate", new { url = "https://example.com/page2" }),
            ScriptedChatClient.ToolCall("done", new { summary = "clicked through two pages" }),
            ScriptedChatClient.Text("stopping"),
            // Synthesis turn — separate GetResponseAsync invocation, no tools.
            ScriptedChatClient.Text("Navigate home then click the details link.\n\n## Flow\n1. open /page1\n2. click through to /page2"));
        var (ctx, _) = TestContext.Build();

        var result = await capability.ExecuteAsync(
            TestContext.Request("browser-task",
                """{"intent":"follow the page chain","allowedHosts":["example.com"]}"""),
            ctx);

        Assert.Equal(AgentTaskState.Completed, result.State);
        var learned = skills.Saved.Keys.SingleOrDefault(k => k.StartsWith("sites/example-com/learned/"));
        Assert.NotNull(learned);
        var skill = skills.Saved[learned!];
        Assert.Equal("Navigate home then click the details link.", skill.Summary);
        Assert.Contains("Flow", skill.Content);
        Assert.Contains("page1", skill.Content);
    }

    [Fact]
    public async Task LearnedSkill_IsNotWritten_OnSingleNavigationTask()
    {
        var skills = new FakeSkillStore();

        var (capability, _, _, _, _, _) = BuildFull(
            skills,
            new FakeLongTermMemory(),
            ScriptedChatClient.ToolCall("done", new { summary = "read it", result = "ok" }),
            ScriptedChatClient.Text("stopping"));
        var (ctx, _) = TestContext.Build();

        await capability.ExecuteAsync(
            TestContext.Request("browser-task",
                """{"intent":"read one page","url":"https://example.com/","allowedHosts":["example.com"]}"""),
            ctx);

        Assert.DoesNotContain(skills.Saved.Keys, k => k.StartsWith("sites/example-com/learned/"));
    }

    private sealed class StubCredentialBroker : ICredentialBroker
    {
        public Task<CredentialReference> ResolveAsync(string credentialId, CancellationToken cancellationToken = default) =>
            throw new CredentialNotFoundException(credentialId);
    }
}
