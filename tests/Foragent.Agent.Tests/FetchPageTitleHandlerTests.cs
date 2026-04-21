using System.Net;
using System.Text;
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
    public async Task ReturnsTitle_FromHtml()
    {
        var (handler, _) = CreateHandler(
            (req, ct) => Respond(HttpStatusCode.OK, "<html><head><title>Hello World</title></head></html>"));

        var result = await InvokeAsync(handler, "fetch-page-title", "https://example.com");

        Assert.Equal(AgentTaskState.Completed, result.Result.State);
        Assert.Equal("Hello World", TextOf(result.Result));
    }

    [Fact]
    public async Task DecodesHtmlEntities()
    {
        var (handler, _) = CreateHandler(
            (req, ct) => Respond(HttpStatusCode.OK, "<title>AT&amp;T &mdash; Home</title>"));

        var result = await InvokeAsync(handler, "fetch-page-title", "https://example.com");

        Assert.Equal("AT&T \u2014 Home", TextOf(result.Result));
    }

    [Fact]
    public async Task ReportsNoTitle_WhenTitleMissing()
    {
        var (handler, _) = CreateHandler(
            (req, ct) => Respond(HttpStatusCode.OK, "<html><body>no head</body></html>"));

        var result = await InvokeAsync(handler, "fetch-page-title", "https://example.com");

        Assert.Equal("(no title)", TextOf(result.Result));
    }

    [Fact]
    public async Task ReportsError_OnNon2xx()
    {
        var (handler, _) = CreateHandler(
            (req, ct) => Respond(HttpStatusCode.NotFound, "not found"));

        var result = await InvokeAsync(handler, "fetch-page-title", "https://example.com");

        Assert.Equal(AgentTaskState.Completed, result.Result.State);
        Assert.Contains("Fetch failed", TextOf(result.Result));
    }

    [Fact]
    public async Task RejectsNonAbsoluteUrl_WithoutHttpCall()
    {
        var calls = 0;
        var (handler, _) = CreateHandler((req, ct) =>
        {
            calls++;
            return Respond(HttpStatusCode.OK, string.Empty);
        });

        var result = await InvokeAsync(handler, "fetch-page-title", "not a url");

        Assert.Equal(0, calls);
        Assert.Contains("absolute http(s) URL", TextOf(result.Result));
    }

    [Fact]
    public async Task RejectsUnknownSkill()
    {
        var (handler, _) = CreateHandler((req, ct) => Respond(HttpStatusCode.OK, string.Empty));

        var result = await InvokeAsync(handler, "other-skill", "https://example.com");

        Assert.Contains("Unknown skill", TextOf(result.Result));
    }

    [Fact]
    public async Task PublishesWorkingStatus_BeforeResult()
    {
        var (handler, _) = CreateHandler(
            (req, ct) => Respond(HttpStatusCode.OK, "<title>t</title>"));

        var result = await InvokeAsync(handler, "fetch-page-title", "https://example.com");

        Assert.Single(result.Capture.Statuses);
        Assert.Equal(AgentTaskState.Working, result.Capture.Statuses[0].State);
        Assert.Equal(AgentTaskState.Completed, result.Result.State);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static (ForagentTaskHandler handler, StatusCapture capture) CreateHandler(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond)
    {
        var httpClient = new HttpClient(new StubHttpMessageHandler(respond));
        var handler = new ForagentTaskHandler(httpClient, NullLogger<ForagentTaskHandler>.Instance);
        return (handler, new StatusCapture());
    }

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

    private static HttpResponseMessage Respond(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/html")
    };

    private static string TextOf(AgentTaskResult result) =>
        result.Message?.Parts.FirstOrDefault(p => p.Kind == "text")?.Text ?? string.Empty;

    private sealed class StatusCapture
    {
        public List<AgentTaskStatusUpdate> Statuses { get; } = [];
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request, cancellationToken));
    }
}
