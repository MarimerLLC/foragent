using Microsoft.Extensions.AI;

namespace Foragent.Agent.Tests.BrowserTask;

/// <summary>
/// Raw IChatClient that plays a scripted sequence of assistant turns. The
/// production IChatClient injected into <c>BrowserTaskCapability</c> is the
/// RockBot-wrapped <see cref="FunctionInvokingChatClient"/>, so in tests we
/// build the same shape via <see cref="ChatClientBuilder"/> +
/// <see cref="FunctionInvokingChatClientBuilderExtensions.UseFunctionInvocation"/>.
/// </summary>
internal sealed class ScriptedChatClient : IChatClient
{
    private readonly Queue<ChatResponse> _responses;

    public int Turns { get; private set; }

    /// <summary>Messages passed on the most recent <see cref="GetResponseAsync"/> call.</summary>
    public IReadOnlyList<ChatMessage> LastMessages { get; private set; } = [];

    /// <summary>Messages passed on the first <see cref="GetResponseAsync"/> call — the planner's initial prompt, before any tool results.</summary>
    public IReadOnlyList<ChatMessage> FirstMessages { get; private set; } = [];

    public ScriptedChatClient(params ChatResponse[] responses)
    {
        _responses = new Queue<ChatResponse>(responses);
    }

    public static ChatResponse Text(string text) =>
        new(new ChatMessage(ChatRole.Assistant, text));

    public static ChatResponse ToolCall(string name, object? args = null)
    {
        var id = $"call_{Guid.NewGuid():N}";
        var dict = args is IDictionary<string, object?> d
            ? (IDictionary<string, object?>)d
            : ObjectToDictionary(args);
        var call = new FunctionCallContent(id, name, dict);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, [call]));
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var captured = messages.ToArray();
        Turns++;
        LastMessages = captured;
        if (FirstMessages.Count == 0)
            FirstMessages = captured;
        if (_responses.Count == 0)
            return Task.FromResult(Text("(script exhausted — stopping)"));
        return Task.FromResult(_responses.Dequeue());
    }

#pragma warning disable CS1998
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield break;
    }
#pragma warning restore CS1998

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }

    private static Dictionary<string, object?> ObjectToDictionary(object? source)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (source is null) return result;
        foreach (var p in source.GetType().GetProperties())
            result[p.Name] = p.GetValue(source);
        return result;
    }
}
