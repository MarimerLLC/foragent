using Foragent.Browser;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using RockBot.A2A;
using RockBot.Host;
using RockBot.Messaging;

namespace Foragent.Agent.Tests;

/// <summary>
/// Shared test doubles and helpers for capability / dispatcher unit tests.
/// </summary>
internal static class TestContext
{
    public static AgentTaskRequest Request(string skill, string text)
    {
        return new AgentTaskRequest
        {
            TaskId = Guid.NewGuid().ToString(),
            ContextId = "ctx",
            Skill = skill,
            Message = new AgentMessage
            {
                Role = "user",
                Parts = [new AgentMessagePart { Kind = "text", Text = text }]
            }
        };
    }

    public static AgentTaskRequest RequestWithMetadata(
        string skill,
        string? text = null,
        IReadOnlyDictionary<string, string>? messageMetadata = null,
        IReadOnlyDictionary<string, string>? requestMetadata = null)
    {
        var parts = text is null
            ? Array.Empty<AgentMessagePart>()
            : [new AgentMessagePart { Kind = "text", Text = text }];
        return new AgentTaskRequest
        {
            TaskId = Guid.NewGuid().ToString(),
            ContextId = "ctx",
            Skill = skill,
            Metadata = requestMetadata,
            Message = new AgentMessage
            {
                Role = "user",
                Parts = parts,
                Metadata = messageMetadata
            }
        };
    }

    public static (AgentTaskContext Context, StatusCapture Capture) Build()
    {
        var capture = new StatusCapture();
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
        return (context, capture);
    }

    public static string TextOf(AgentTaskResult result) =>
        result.Message?.Parts.FirstOrDefault(p => p.Kind == "text")?.Text ?? string.Empty;
}

internal sealed class StatusCapture
{
    public List<AgentTaskStatusUpdate> Statuses { get; } = [];
}

internal sealed class StubBrowserSessionFactory : IBrowserSessionFactory
{
    public Func<Uri, CancellationToken, Task<string?>> TitleResponder { get; set; } =
        (_, _) => Task.FromResult<string?>(null);

    public Func<Uri, CancellationToken, Task<PageSnapshot>> SnapshotResponder { get; set; } =
        (url, _) => Task.FromResult(new PageSnapshot(url, "stub", "stub content", PageSnapshotSource.Accessibility));

    public int SessionsCreated { get; private set; }
    public int SessionsDisposed { get; private set; }

    public Task<IBrowserSession> CreateSessionAsync(CancellationToken ct = default)
    {
        SessionsCreated++;
        return Task.FromResult<IBrowserSession>(new StubSession(this));
    }

    private sealed class StubSession(StubBrowserSessionFactory owner) : IBrowserSession
    {
        public Task<string?> FetchPageTitleAsync(Uri url, CancellationToken ct = default) =>
            owner.TitleResponder(url, ct);

        public Task<PageSnapshot> CapturePageSnapshotAsync(Uri url, CancellationToken ct = default) =>
            owner.SnapshotResponder(url, ct);

        public ValueTask DisposeAsync()
        {
            owner.SessionsDisposed++;
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class StubChatClient(Func<IEnumerable<ChatMessage>, ChatOptions?, Task<ChatResponse>> responder)
    : IChatClient
{
    public int CallCount { get; private set; }
    public List<ChatMessage> LastMessages { get; } = [];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastMessages.Clear();
        LastMessages.AddRange(chatMessages);
        return responder(chatMessages, options);
    }

#pragma warning disable CS1998 // async iterator with no awaits
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield break;
    }
#pragma warning restore CS1998

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
