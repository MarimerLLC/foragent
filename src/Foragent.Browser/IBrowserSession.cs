namespace Foragent.Browser;

/// <summary>
/// Represents an isolated browser context for a single task.
/// Each A2A task receives its own browser context; the underlying browser
/// instance is long-lived and shared.
/// </summary>
public interface IBrowserSession
{
}
