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
    public Func<Uri, CancellationToken, Task<IBrowserPage>> PageResponder { get; set; } =
        (_, _) => Task.FromResult<IBrowserPage>(new StubBrowserPage());

    public int SessionsCreated { get; private set; }
    public int SessionsDisposed { get; private set; }

    public Task<IBrowserSession> CreateSessionAsync(CancellationToken ct = default)
    {
        SessionsCreated++;
        return Task.FromResult<IBrowserSession>(new StubSession(this));
    }

    public Task<IBrowserSession> CreateSessionAsync(Func<Uri, bool> allowedHost, CancellationToken ct = default)
    {
        SessionsCreated++;
        return Task.FromResult<IBrowserSession>(new StubSession(this));
    }

    private sealed class StubSession(StubBrowserSessionFactory owner) : IBrowserSession
    {
        public Task<IBrowserPage> OpenPageAsync(Uri url, CancellationToken ct = default) =>
            owner.PageResponder(url, ct);

        public Task<IBrowserAgentPage> OpenAgentPageAsync(CancellationToken ct = default) =>
            throw new NotSupportedException("StubBrowserSessionFactory does not expose an agent page; test BrowserTaskCapability with a dedicated fake.");

        public ValueTask DisposeAsync()
        {
            owner.SessionsDisposed++;
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class StubBrowserPage : IBrowserPage
{
    public List<string> Actions { get; } = [];
    public Uri CurrentUrl { get; set; } = new("https://stub.example/");
    public HashSet<string> TimeoutSelectors { get; } = new(StringComparer.Ordinal);
    public Action<string>? OnClick { get; set; }

    public Task NavigateAsync(Uri url, CancellationToken ct = default)
    {
        CurrentUrl = url;
        Actions.Add($"navigate:{url}");
        return Task.CompletedTask;
    }

    public Task FillAsync(string selector, string value, CancellationToken ct = default)
    {
        // Record the selector but not the value — tests must never
        // accidentally assert on values that could include sensitive input.
        Actions.Add($"fill:{selector}");
        return Task.CompletedTask;
    }

    public Task ClickAsync(string selector, CancellationToken ct = default)
    {
        Actions.Add($"click:{selector}");
        OnClick?.Invoke(selector);
        return Task.CompletedTask;
    }

    public Task WaitForSelectorAsync(string selector, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        Actions.Add($"wait:{selector}");
        if (TimeoutSelectors.Contains(selector))
            throw new TimeoutException($"Stub: '{selector}' marked as a timeout.");
        return Task.CompletedTask;
    }

    public Task<Uri> GetUrlAsync(CancellationToken ct = default) => Task.FromResult(CurrentUrl);

    public Task<string?> GetTextAsync(string selector, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    public FormScan? FormScan { get; set; }

    public Task<FormScan?> ScanFormAsync(string? formSelector = null, CancellationToken ct = default)
    {
        Actions.Add($"scan:{formSelector ?? "<null>"}");
        return Task.FromResult(FormScan);
    }

    public Task SelectOptionAsync(string selector, string value, CancellationToken ct = default)
    {
        Actions.Add($"select:{selector}:{value}");
        return Task.CompletedTask;
    }

    public Task SetCheckedAsync(string selector, bool checked_, CancellationToken ct = default)
    {
        Actions.Add($"checked:{selector}:{checked_}");
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// In-memory <see cref="ISkillStore"/> that ignores the query embedding and
/// returns saved skills in insertion order. Sufficient for testing the
/// priming / learned-skill paths without spinning up FileSkillStore.
/// </summary>
internal sealed class FakeSkillStore : ISkillStore
{
    public Dictionary<string, Skill> Saved { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Dictionary<string, string>> Resources { get; } = new(StringComparer.Ordinal);
    public List<(string Query, int MaxResults)> Searches { get; } = [];

    public Task SaveAsync(Skill skill)
    {
        // Match FileSkillStore's behavior: SaveAsync(skill) alone preserves
        // the existing manifest (see rockbot commit 2db3775 fix #1).
        if (skill.Manifest is null && Saved.TryGetValue(skill.Name, out var prior) && prior.Manifest is not null)
            skill = skill with { Manifest = prior.Manifest };
        Saved[skill.Name] = skill;
        return Task.CompletedTask;
    }

    public Task SaveAsync(Skill skill, IReadOnlyList<SkillResourceInput>? resources)
    {
        if (resources is null || resources.Count == 0)
            return SaveAsync(skill);

        var manifest = resources.Select(r => new SkillResource(r.Filename, r.Type, r.Description)).ToList();
        var bundled = skill with { Manifest = manifest };
        Saved[skill.Name] = bundled;
        Resources[skill.Name] = resources.ToDictionary(r => r.Filename, r => r.Content, StringComparer.Ordinal);
        return Task.CompletedTask;
    }

    public Task<Skill?> GetAsync(string name) =>
        Task.FromResult(Saved.TryGetValue(name, out var skill) ? skill : null);

    public Task<string?> GetResourceAsync(string skillName, string filename)
    {
        if (Resources.TryGetValue(skillName, out var bundle) && bundle.TryGetValue(filename, out var content))
            return Task.FromResult<string?>(content);
        return Task.FromResult<string?>(null);
    }

    public Task<IReadOnlyList<Skill>> ListAsync() =>
        Task.FromResult<IReadOnlyList<Skill>>([.. Saved.Values]);

    public Task DeleteAsync(string name)
    {
        Saved.Remove(name);
        Resources.Remove(name);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Skill>> SearchAsync(
        string query, int maxResults, CancellationToken cancellationToken = default, float[]? queryEmbedding = null)
    {
        Searches.Add((query, maxResults));
        return Task.FromResult<IReadOnlyList<Skill>>([.. Saved.Values.Take(maxResults)]);
    }
}

/// <summary>
/// In-memory <see cref="ILongTermMemory"/>; search returns entries whose
/// content mentions the query (case-insensitive). Not intended to match the
/// FileMemoryStore ranking — just enough to drive priming tests.
/// </summary>
internal sealed class FakeLongTermMemory : ILongTermMemory
{
    public Dictionary<string, MemoryEntry> Saved { get; } = new(StringComparer.Ordinal);

    public Task SaveAsync(MemoryEntry entry, CancellationToken cancellationToken)
    {
        Saved[entry.Id] = entry;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MemoryEntry>> SearchAsync(MemorySearchCriteria criteria, CancellationToken cancellationToken)
    {
        IEnumerable<MemoryEntry> matches = Saved.Values;
        if (!string.IsNullOrEmpty(criteria.Query))
            matches = matches.Where(m => m.Content.Contains(criteria.Query, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult<IReadOnlyList<MemoryEntry>>([.. matches.Take(criteria.MaxResults)]);
    }

    public Task<MemoryEntry?> GetAsync(string id, CancellationToken cancellationToken) =>
        Task.FromResult(Saved.TryGetValue(id, out var entry) ? entry : null);

    public Task DeleteAsync(string id, CancellationToken cancellationToken)
    {
        Saved.Remove(id);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListTagsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([.. Saved.Values.SelectMany(m => m.Tags).Distinct()]);

    public Task<IReadOnlyList<string>> ListCategoriesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([.. Saved.Values.Select(m => m.Category).OfType<string>().Distinct()]);
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
