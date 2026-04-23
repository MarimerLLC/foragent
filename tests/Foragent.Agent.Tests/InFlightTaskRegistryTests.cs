using Foragent.Capabilities;
using Xunit;

namespace Foragent.Agent.Tests;

public class InFlightTaskRegistryTests
{
    [Fact]
    public void Register_ReturnsLinkedToken_ThatFiresWhenCancelled()
    {
        var registry = new InFlightTaskRegistry();
        using var parent = new CancellationTokenSource();

        var token = registry.Register("task-1", parent.Token);

        Assert.False(token.IsCancellationRequested);
        Assert.True(registry.TryCancel("task-1"));
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void Register_ReturnsLinkedToken_ThatFiresWhenParentCancels()
    {
        var registry = new InFlightTaskRegistry();
        using var parent = new CancellationTokenSource();

        var token = registry.Register("task-1", parent.Token);

        parent.Cancel();
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void TryCancel_ReturnsFalse_ForUnknownTask()
    {
        var registry = new InFlightTaskRegistry();

        Assert.False(registry.TryCancel("nope"));
    }

    [Fact]
    public void Remove_DropsRegistration()
    {
        var registry = new InFlightTaskRegistry();
        using var parent = new CancellationTokenSource();
        registry.Register("task-1", parent.Token);
        Assert.Equal(1, registry.Count);

        registry.Remove("task-1");

        Assert.Equal(0, registry.Count);
        Assert.False(registry.TryCancel("task-1"));
    }

    [Fact]
    public void Register_ReplacesPriorRegistration_AndCancelsIt()
    {
        // A redelivered message with the same task id should not orphan the
        // previous registration's CTS. The first CTS is cancelled so any
        // residual work unwinds before the new execution begins.
        var registry = new InFlightTaskRegistry();
        using var parent = new CancellationTokenSource();

        var firstToken = registry.Register("task-1", parent.Token);
        var secondToken = registry.Register("task-1", parent.Token);

        Assert.True(firstToken.IsCancellationRequested);
        Assert.False(secondToken.IsCancellationRequested);
        Assert.Equal(1, registry.Count);
    }
}
