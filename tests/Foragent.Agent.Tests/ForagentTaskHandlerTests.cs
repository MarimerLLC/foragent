using Foragent.Capabilities;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.A2A;
using Xunit;

namespace Foragent.Agent.Tests;

public class ForagentTaskHandlerTests
{
    [Fact]
    public async Task DispatchesToMatchingCapability()
    {
        var executed = new List<string>();
        ICapability alpha = new TrackingCapability("alpha", executed);
        ICapability beta = new TrackingCapability("beta", executed);
        var handler = new ForagentTaskHandler([alpha, beta], new InFlightTaskRegistry(), NullLogger<ForagentTaskHandler>.Instance);
        var (context, _) = TestContext.Build();

        await handler.HandleTaskAsync(TestContext.Request("beta", "in"), context);

        Assert.Equal(["beta"], executed);
    }

    [Fact]
    public async Task PublishesWorkingStatus_BeforeDispatch()
    {
        ICapability alpha = new TrackingCapability("alpha", []);
        var handler = new ForagentTaskHandler([alpha], new InFlightTaskRegistry(), NullLogger<ForagentTaskHandler>.Instance);
        var (context, capture) = TestContext.Build();

        await handler.HandleTaskAsync(TestContext.Request("alpha", "in"), context);

        Assert.Single(capture.Statuses);
        Assert.Equal(AgentTaskState.Working, capture.Statuses[0].State);
    }

    [Fact]
    public async Task ReturnsErrorResult_ForUnknownSkill()
    {
        ICapability alpha = new TrackingCapability("alpha", []);
        var handler = new ForagentTaskHandler([alpha], new InFlightTaskRegistry(), NullLogger<ForagentTaskHandler>.Instance);
        var (context, _) = TestContext.Build();

        var result = await handler.HandleTaskAsync(TestContext.Request("nope", "in"), context);

        Assert.Equal(AgentTaskState.Completed, result.State);
        Assert.Contains("Unknown skill 'nope'", TestContext.TextOf(result));
        Assert.Contains("alpha", TestContext.TextOf(result));
    }

    [Fact]
    public async Task RegistersTask_ForDurationOfExecution_ThenRemovesOnCompletion()
    {
        var registry = new InFlightTaskRegistry();
        CancellationToken? observedCt = null;
        int countDuringExecute = 0;
        var capability = new CallbackCapability("probe", (req, ctx) =>
        {
            countDuringExecute = registry.Count;
            observedCt = ctx.MessageContext.CancellationToken;
            return Task.FromResult(CapabilityResultFactory.Completed(req, "ok"));
        });
        var handler = new ForagentTaskHandler([capability], registry, NullLogger<ForagentTaskHandler>.Instance);
        var (context, _) = TestContext.Build();

        await handler.HandleTaskAsync(TestContext.Request("probe", "in"), context);

        Assert.Equal(1, countDuringExecute);
        Assert.Equal(0, registry.Count);
        Assert.NotNull(observedCt);
    }

    [Fact]
    public async Task ExternalCancel_CancelsTheTaskToken()
    {
        // Simulates the cancel handler firing mid-execution: the capability's
        // observed CT should be cancelled when TryCancel is called, even
        // though the parent message CT is still live.
        var registry = new InFlightTaskRegistry();
        var observedCt = new TaskCompletionSource<CancellationToken>();
        var released = new TaskCompletionSource();
        var capability = new CallbackCapability("probe", async (req, ctx) =>
        {
            observedCt.TrySetResult(ctx.MessageContext.CancellationToken);
            await released.Task;
            return CapabilityResultFactory.Completed(req, "ok");
        });
        var handler = new ForagentTaskHandler([capability], registry, NullLogger<ForagentTaskHandler>.Instance);
        var (context, _) = TestContext.Build();
        var request = TestContext.Request("probe", "in");

        var task = handler.HandleTaskAsync(request, context);
        var ct = await observedCt.Task;

        Assert.False(ct.IsCancellationRequested);
        Assert.True(registry.TryCancel(request.TaskId));
        Assert.True(ct.IsCancellationRequested);

        released.SetResult();
        await task;
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task Registry_IsClearedEven_WhenCapabilityThrows()
    {
        var registry = new InFlightTaskRegistry();
        var capability = new CallbackCapability("probe", (_, _) =>
            throw new InvalidOperationException("boom"));
        var handler = new ForagentTaskHandler([capability], registry, NullLogger<ForagentTaskHandler>.Instance);
        var (context, _) = TestContext.Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleTaskAsync(TestContext.Request("probe", "in"), context));

        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task SkillLookup_IsCaseInsensitive()
    {
        var executed = new List<string>();
        ICapability alpha = new TrackingCapability("alpha", executed);
        var handler = new ForagentTaskHandler([alpha], new InFlightTaskRegistry(), NullLogger<ForagentTaskHandler>.Instance);
        var (context, _) = TestContext.Build();

        await handler.HandleTaskAsync(TestContext.Request("Alpha", "in"), context);

        Assert.Equal(["alpha"], executed);
    }

    private sealed class CallbackCapability(
        string skillId,
        Func<AgentTaskRequest, AgentTaskContext, Task<AgentTaskResult>> callback) : ICapability
    {
        public string SkillId => skillId;
        public AgentSkill Skill => new() { Id = skillId, Name = skillId, Description = skillId };

        public Task<AgentTaskResult> ExecuteAsync(AgentTaskRequest request, AgentTaskContext context) =>
            callback(request, context);
    }

    private sealed class TrackingCapability(string skillId, List<string> executed) : ICapability
    {
        public string SkillId => skillId;
        public AgentSkill Skill => new() { Id = skillId, Name = skillId, Description = skillId };

        public Task<AgentTaskResult> ExecuteAsync(AgentTaskRequest request, AgentTaskContext context)
        {
            executed.Add(skillId);
            return Task.FromResult(CapabilityResultFactory.Completed(request, "ok"));
        }
    }

    // Mirrors internal CapabilityResult.Completed — keeps the test independent
    // of the internal helper while producing equivalent results.
    private static class CapabilityResultFactory
    {
        public static AgentTaskResult Completed(AgentTaskRequest request, string text) => new()
        {
            TaskId = request.TaskId,
            ContextId = request.ContextId,
            State = AgentTaskState.Completed,
            Message = new AgentMessage
            {
                Role = "agent",
                Parts = [new AgentMessagePart { Kind = "text", Text = text }]
            }
        };
    }
}
