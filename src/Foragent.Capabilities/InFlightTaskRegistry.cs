using System.Collections.Concurrent;

namespace Foragent.Capabilities;

/// <summary>
/// Tracks in-flight A2A tasks so an out-of-band cancel message can reach the
/// running task's <see cref="CancellationToken"/>. The framework's default
/// <c>AgentTaskCancelHandler</c> assumes stateless agents and always replies
/// <c>TaskNotCancelable</c>; Foragent is stateful (long-running browser work
/// per task), so we replace it with a registry-aware handler.
///
/// One instance per process — the registry crosses DI scopes because the
/// cancel message is handled in a different scope than the task request.
/// </summary>
public sealed class InFlightTaskRegistry
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _tokens = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates a linked <see cref="CancellationTokenSource"/> for
    /// <paramref name="taskId"/>, registers it, and returns the linked token
    /// that the running task should observe. If the task id is already
    /// registered — the framework redelivered a message — the prior
    /// registration is cancelled first so the stale task unwinds before the
    /// new one starts.
    /// </summary>
    public CancellationToken Register(string taskId, CancellationToken parentToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        _tokens.AddOrUpdate(
            taskId,
            _ => cts,
            (_, existing) =>
            {
                // A redelivered task id should not keep the prior CTS alive —
                // cancel it so any residual work stops, then replace.
                try { existing.Cancel(); } catch (ObjectDisposedException) { }
                existing.Dispose();
                return cts;
            });
        return cts.Token;
    }

    /// <summary>
    /// Cancels the task if registered. Returns true when a cancel was issued,
    /// false when no task with that id is in flight.
    /// </summary>
    public bool TryCancel(string taskId)
    {
        if (!_tokens.TryGetValue(taskId, out var cts))
            return false;
        try
        {
            cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            // Race: the task finished and disposed its CTS between our
            // TryGetValue and Cancel. Equivalent to not-found for the caller.
            return false;
        }
    }

    /// <summary>
    /// Removes and disposes the registration for <paramref name="taskId"/>.
    /// Called from the task handler's <c>finally</c> so both normal completion
    /// and failure clean up.
    /// </summary>
    public void Remove(string taskId)
    {
        if (_tokens.TryRemove(taskId, out var cts))
            cts.Dispose();
    }

    /// <summary>Count of in-flight registrations. Exposed for tests and diagnostics.</summary>
    public int Count => _tokens.Count;
}
