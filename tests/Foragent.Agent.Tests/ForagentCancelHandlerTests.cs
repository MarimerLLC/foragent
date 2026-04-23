using System.Text.Json;
using Foragent.Capabilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.A2A;
using RockBot.Host;
using RockBot.Messaging;
using Xunit;

namespace Foragent.Agent.Tests;

public class ForagentCancelHandlerTests
{
    [Fact]
    public async Task CancelsInFlightTask_WithoutPublishingReply()
    {
        var registry = new InFlightTaskRegistry();
        using var parent = new CancellationTokenSource();
        var token = registry.Register("task-1", parent.Token);

        var publisher = new CapturingPublisher();
        var handler = BuildHandler(registry, publisher);

        await handler.HandleAsync(
            new AgentTaskCancelRequest { TaskId = "task-1" },
            BuildContext());

        Assert.True(token.IsCancellationRequested);
        // On a successful cancel we deliberately don't publish a reply —
        // the task's own terminal result is the acknowledgment.
        Assert.Empty(publisher.Published);
    }

    [Fact]
    public async Task PublishesTaskNotFound_WhenNoInFlightRegistration()
    {
        var registry = new InFlightTaskRegistry();
        var publisher = new CapturingPublisher();
        var handler = BuildHandler(registry, publisher);

        await handler.HandleAsync(
            new AgentTaskCancelRequest { TaskId = "nope", ContextId = "ctx-7" },
            BuildContext(replyTo: "caller.replies"));

        var (topic, envelope) = Assert.Single(publisher.Published);
        Assert.Equal("caller.replies", topic);
        // Use the framework's GetPayload extension so the camelCase naming
        // policy matches what ToEnvelope used on the write side.
        var error = envelope.GetPayload<AgentTaskError>()!;
        Assert.Equal("nope", error.TaskId);
        Assert.Equal("ctx-7", error.ContextId);
        Assert.Equal(AgentTaskError.Codes.TaskNotFound, error.Code);
    }

    [Fact]
    public async Task FallsBackToDefaultResultTopic_WhenEnvelopeHasNoReplyTo()
    {
        var registry = new InFlightTaskRegistry();
        var publisher = new CapturingPublisher();
        var handler = BuildHandler(registry, publisher);

        await handler.HandleAsync(
            new AgentTaskCancelRequest { TaskId = "missing" },
            BuildContext(replyTo: null));

        var (topic, _) = Assert.Single(publisher.Published);
        Assert.Equal("agent.response", topic); // A2AOptions.DefaultResultTopic default
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static ForagentCancelHandler BuildHandler(
        InFlightTaskRegistry registry,
        IMessagePublisher publisher)
    {
        var options = new A2AOptions();
        var identity = new AgentIdentity("Foragent");
        return new ForagentCancelHandler(
            registry, publisher, options, identity,
            NullLogger<ForagentCancelHandler>.Instance);
    }

    private static MessageHandlerContext BuildContext(string? replyTo = null)
    {
        var envelope = MessageEnvelope.Create(
            messageType: typeof(AgentTaskCancelRequest).FullName!,
            body: ReadOnlyMemory<byte>.Empty,
            source: "test",
            replyTo: replyTo);
        return new MessageHandlerContext
        {
            Envelope = envelope,
            Agent = new AgentIdentity("Foragent"),
            Services = new ServiceCollection().BuildServiceProvider(),
            CancellationToken = CancellationToken.None
        };
    }

    private sealed class CapturingPublisher : IMessagePublisher
    {
        public List<(string Topic, MessageEnvelope Envelope)> Published { get; } = [];

        public Task PublishAsync(string topic, MessageEnvelope envelope, CancellationToken cancellationToken = default)
        {
            Published.Add((topic, envelope));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
