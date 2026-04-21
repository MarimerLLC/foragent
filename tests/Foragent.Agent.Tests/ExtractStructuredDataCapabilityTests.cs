using Foragent.Browser;
using Foragent.Capabilities;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.A2A;
using Xunit;

namespace Foragent.Agent.Tests;

public class ExtractStructuredDataCapabilityTests
{
    private const string ValidInput = """
        {"url":"https://example.com","description":"the main heading"}
        """;

    [Fact]
    public async Task ReturnsLlmText_OnSuccess()
    {
        var factory = new StubBrowserSessionFactory();
        var chat = new StubChatClient((_, _) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"heading":"Example"}"""))));
        var capability = new ExtractStructuredDataCapability(
            factory, chat, NullLogger<ExtractStructuredDataCapability>.Instance);
        var (context, _) = TestContext.Build();

        var result = await capability.ExecuteAsync(
            TestContext.Request("extract-structured-data", ValidInput),
            context);

        Assert.Equal(AgentTaskState.Completed, result.State);
        Assert.Equal("""{"heading":"Example"}""", TestContext.TextOf(result));
    }

    [Fact]
    public async Task PromptIncludesUrl_Description_AndSnapshot()
    {
        var factory = new StubBrowserSessionFactory
        {
            SnapshotResponder = (url, _) =>
                Task.FromResult(new PageSnapshot(url, "Page Title", "body - Hello", PageSnapshotSource.Accessibility))
        };
        var chat = new StubChatClient((_, _) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}"))));
        var capability = new ExtractStructuredDataCapability(
            factory, chat, NullLogger<ExtractStructuredDataCapability>.Instance);
        var (context, _) = TestContext.Build();

        await capability.ExecuteAsync(
            TestContext.Request("extract-structured-data", ValidInput),
            context);

        Assert.Equal(1, chat.CallCount);
        var userMessage = chat.LastMessages.First(m => m.Role == ChatRole.User).Text ?? string.Empty;
        Assert.Contains("https://example.com", userMessage);
        Assert.Contains("the main heading", userMessage);
        Assert.Contains("body - Hello", userMessage);
        Assert.Contains("Page Title", userMessage);
    }

    [Fact]
    public async Task RequestsJsonResponseFormat()
    {
        ChatOptions? capturedOptions = null;
        var factory = new StubBrowserSessionFactory();
        var chat = new StubChatClient((_, opts) =>
        {
            capturedOptions = opts;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}")));
        });
        var capability = new ExtractStructuredDataCapability(
            factory, chat, NullLogger<ExtractStructuredDataCapability>.Instance);
        var (context, _) = TestContext.Build();

        await capability.ExecuteAsync(
            TestContext.Request("extract-structured-data", ValidInput),
            context);

        Assert.NotNull(capturedOptions?.ResponseFormat);
        Assert.IsType<ChatResponseFormatJson>(capturedOptions!.ResponseFormat);
    }

    [Fact]
    public async Task RejectsMissingUrl()
    {
        var factory = new StubBrowserSessionFactory();
        var chat = new StubChatClient((_, _) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}"))));
        var capability = new ExtractStructuredDataCapability(
            factory, chat, NullLogger<ExtractStructuredDataCapability>.Instance);
        var (context, _) = TestContext.Build();

        var result = await capability.ExecuteAsync(
            TestContext.Request("extract-structured-data", """{"description":"no url"}"""),
            context);

        Assert.Equal(0, factory.SessionsCreated);
        Assert.Equal(0, chat.CallCount);
        Assert.Contains("URL", TestContext.TextOf(result));
    }

    [Fact]
    public async Task RejectsMissingDescription()
    {
        var factory = new StubBrowserSessionFactory();
        var chat = new StubChatClient((_, _) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}"))));
        var capability = new ExtractStructuredDataCapability(
            factory, chat, NullLogger<ExtractStructuredDataCapability>.Instance);
        var (context, _) = TestContext.Build();

        var result = await capability.ExecuteAsync(
            TestContext.Request("extract-structured-data", """{"url":"https://example.com"}"""),
            context);

        Assert.Equal(0, factory.SessionsCreated);
        Assert.Equal(0, chat.CallCount);
        Assert.Contains("description", TestContext.TextOf(result));
    }

    [Fact]
    public async Task ReportsError_WhenBrowserThrows()
    {
        var factory = new StubBrowserSessionFactory
        {
            SnapshotResponder = (_, _) => Task.FromException<PageSnapshot>(new InvalidOperationException("nav failed"))
        };
        var chat = new StubChatClient((_, _) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}"))));
        var capability = new ExtractStructuredDataCapability(
            factory, chat, NullLogger<ExtractStructuredDataCapability>.Instance);
        var (context, _) = TestContext.Build();

        var result = await capability.ExecuteAsync(
            TestContext.Request("extract-structured-data", ValidInput),
            context);

        Assert.Contains("Fetch failed: nav failed", TestContext.TextOf(result));
        Assert.Equal(0, chat.CallCount);
    }

    [Fact]
    public async Task ReportsError_WhenLlmThrows()
    {
        var factory = new StubBrowserSessionFactory();
        var chat = new StubChatClient((_, _) =>
            Task.FromException<ChatResponse>(new InvalidOperationException("llm boom")));
        var capability = new ExtractStructuredDataCapability(
            factory, chat, NullLogger<ExtractStructuredDataCapability>.Instance);
        var (context, _) = TestContext.Build();

        var result = await capability.ExecuteAsync(
            TestContext.Request("extract-structured-data", ValidInput),
            context);

        Assert.Contains("Extraction failed: llm boom", TestContext.TextOf(result));
    }

    [Fact]
    public async Task AcceptsInputs_FromMetadata()
    {
        Uri? capturedUrl = null;
        var factory = new StubBrowserSessionFactory
        {
            SnapshotResponder = (url, _) =>
            {
                capturedUrl = url;
                return Task.FromResult(new PageSnapshot(url, null, "content", PageSnapshotSource.Accessibility));
            }
        };
        string? descriptionInPrompt = null;
        var chat = new StubChatClient((msgs, _) =>
        {
            descriptionInPrompt = msgs.First(m => m.Role == ChatRole.User).Text;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}")));
        });
        var capability = new ExtractStructuredDataCapability(
            factory, chat, NullLogger<ExtractStructuredDataCapability>.Instance);
        var (context, _) = TestContext.Build();
        var request = TestContext.RequestWithMetadata(
            "extract-structured-data",
            messageMetadata: new Dictionary<string, string>
            {
                ["url"] = "https://metadata.example",
                ["description"] = "the shipping address"
            });

        var result = await capability.ExecuteAsync(request, context);

        Assert.Equal(AgentTaskState.Completed, result.State);
        Assert.Equal(new Uri("https://metadata.example"), capturedUrl);
        Assert.Contains("the shipping address", descriptionInPrompt);
    }

    [Fact]
    public async Task ReportsError_OnEmptyLlmResponse()
    {
        var factory = new StubBrowserSessionFactory();
        var chat = new StubChatClient((_, _) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, ""))));
        var capability = new ExtractStructuredDataCapability(
            factory, chat, NullLogger<ExtractStructuredDataCapability>.Instance);
        var (context, _) = TestContext.Build();

        var result = await capability.ExecuteAsync(
            TestContext.Request("extract-structured-data", ValidInput),
            context);

        Assert.Contains("empty response", TestContext.TextOf(result));
    }
}
