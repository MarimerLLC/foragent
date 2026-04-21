using Foragent.Browser;
using Foragent.Capabilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.A2A;
using RockBot.Host;
using RockBot.Messaging;
using Xunit;

namespace Foragent.Agent.Tests;

public class FetchPageTitleHandlerTests
{
    [Fact]
    public async Task ReturnsTitle_FromBrowser()
    {
        var factory = new StubBrowserSessionFactory((_, _) => Task.FromResult<string?>("Hello World"));
        var handler = new ForagentTaskHandler(factory, NullLogger<ForagentTaskHandler>.Instance);

        var result = await InvokeAsync(handler, "fetch-page-title", "https://example.com");

        Assert.Equal(AgentTaskState.Completed, result.Result.State);
        Assert.Equal("Hello World", TextOf(result.Result));
    }

    [Fact]
    public async Task ReportsNoTitle_WhenBrowserReturnsNull()
    {
        var factory = new StubBrowserSessionFactory((_, _) => Task.FromResult<string?>(null));
        var handler = new ForagentTaskHandler(factory, NullLogger<ForagentTaskHandler>.Instance);

        var result = await InvokeAsync(handler, "fetch-page-title", "https://example.com");

        Assert.Equal("(no title)", TextOf(result.Result));
    }

    [Fact]
    public async Task ReportsError_WhenBrowserThrows()
    {
        var factory = new StubBrowserSessionFactory((_, _) =>
            Task.FromException<string?>(new InvalidOperationException("boom")));
        var handler = new ForagentTaskHandler(factory, NullLogger<ForagentTaskHandler>.Instance);

        var result = await InvokeAsync(handler, "fetch-page-title", "https://example.com");

        Assert.Equal(AgentTaskState.Completed, result.Result.State);
        Assert.Contains("Fetch failed: boom", TextOf(result.Result));
    }

    [Fact]
    public async Task RejectsNonAbsoluteUrl_WithoutCreatingSession()
    {
        var calls = 0;
        var factory = new StubBrowserSessionFactory((_, _) =>
        {
            calls++;
            return Task.FromResult<string?>("ignored");
        });
        var handler = new ForagentTaskHandler(factory, NullLogger<ForagentTaskHandler>.Instance);

        var result = await InvokeAsync(handler, "fetch-page-title", "not a url");

        Assert.Equal(0, calls);
        Assert.Contains("absolute http(s) URL", TextOf(result.Result));
    }

    [Fact]
    public async Task RejectsUnknownSkill()
    {
        var factory = new StubBrowserSessionFactory((_, _) => Task.FromResult<string?>("unused"));
        var handler = new ForagentTaskHandler(factory, NullLogger<ForagentTaskHandler>.Instance);

        var result = await InvokeAsync(handler, "other-skill", "https://example.com");

        Assert.Contains("Unknown skill", TextOf(result.Result));
    }

    [Fact]
    public async Task PublishesWorkingStatus_BeforeResult()
    {
        var factory = new StubBrowserSessionFactory((_, _) => Task.FromResult<string?>("t"));
        var handler = new ForagentTaskHandler(factory, NullLogger<ForagentTaskHandler>.Instance);

        var result = await InvokeAsync(handler, "fetch-page-title", "https://example.com");

        Assert.Single(result.Capture.Statuses);
        Assert.Equal(AgentTaskState.Working, result.Capture.Statuses[0].State);
        Assert.Equal(AgentTaskState.Completed, result.Result.State);
    }

    [Fact]
    public async Task DisposesSession_AfterUse()
    {
        var factory = new StubBrowserSessionFactory((_, _) => Task.FromResult<string?>("t"));
        var handler = new ForagentTaskHandler(factory, NullLogger<ForagentTaskHandler>.Instance);

        await InvokeAsync(handler, "fetch-page-title", "https://example.com");

        Assert.Equal(1, factory.SessionsCreated);
        Assert.Equal(1, factory.SessionsDisposed);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static async Task<(StatusCapture Capture, AgentTaskResult Result)> InvokeAsync(
        ForagentTaskHandler handler, string skill, string input)
    {
        var capture = new StatusCapture();
        var request = new AgentTaskRequest
        {
            TaskId = Guid.NewGuid().ToString(),
            ContextId = "ctx",
            Skill = skill,
            Message = new AgentMessage
            {
                Role = "user",
                Parts = [new AgentMessagePart { Kind = "text", Text = input }]
            }
        };
        var envelope = MessageEnvelope.Create(
            messageType: typeof(AgentTaskRequest).FullName!,
            body: ReadOnlyMemory<byte>.Empty,
            source: "test");
        var messageContext = new MessageHandlerContext
        {
            Envelope = envelope,
            Agent = new AgentIdentity("Foragent"),
            Services = new ServiceCollection().BuildServiceProvider(),
            CancellationToken = CancellationToken.None
        };
        var context = new AgentTaskContext
        {
            MessageContext = messageContext,
            PublishStatus = (update, _) =>
            {
                capture.Statuses.Add(update);
                return Task.CompletedTask;
            }
        };
        var result = await handler.HandleTaskAsync(request, context);
        return (capture, result);
    }

    private static string TextOf(AgentTaskResult result) =>
        result.Message?.Parts.FirstOrDefault(p => p.Kind == "text")?.Text ?? string.Empty;

    private sealed class StatusCapture
    {
        public List<AgentTaskStatusUpdate> Statuses { get; } = [];
    }

    private sealed class StubBrowserSessionFactory(
        Func<Uri, CancellationToken, Task<string?>> responder) : IBrowserSessionFactory
    {
        public int SessionsCreated { get; private set; }
        public int SessionsDisposed { get; private set; }

        public Task<IBrowserSession> CreateSessionAsync(CancellationToken ct = default)
        {
            SessionsCreated++;
            return Task.FromResult<IBrowserSession>(new StubSession(this, responder));
        }

        private sealed class StubSession(
            StubBrowserSessionFactory owner,
            Func<Uri, CancellationToken, Task<string?>> responder) : IBrowserSession
        {
            public Task<string?> FetchPageTitleAsync(Uri url, CancellationToken ct = default) =>
                responder(url, ct);

            public ValueTask DisposeAsync()
            {
                owner.SessionsDisposed++;
                return ValueTask.CompletedTask;
            }
        }
    }
}
