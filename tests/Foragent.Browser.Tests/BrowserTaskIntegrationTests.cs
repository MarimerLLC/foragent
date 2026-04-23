using System.ClientModel;
using System.Text.Json;
using Foragent.Capabilities.BrowserTask;
using Foragent.Credentials;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI;
using RockBot.A2A;
using RockBot.Host;
using RockBot.Llm;
using RockBot.Messaging;
using Xunit;

namespace Foragent.Browser.Tests;

/// <summary>
/// Step 6's "small curated benchmark" (spec §9.1). Three Kestrel-hosted
/// scenarios exercise the real <see cref="BrowserTaskCapability"/> end-to-end
/// with real Chromium + a real LLM. Skipped when <c>FORAGENT_LLM_*</c> env
/// vars are unset, so the main test run stays free of network / API
/// dependencies. Establishes the unaided success floor before step 7 adds
/// learned-skill priming.
/// </summary>
[Collection("Playwright")]
public class BrowserTaskIntegrationTests(TestPageServerFixture fixture)
{
    [SkippableFact]
    public async Task ClickThrough_FindsDestinationText()
    {
        var config = LlmConfig.FromEnvironment();
        Skip.If(config is null, "FORAGENT_LLM_* env vars not set — skipping browser-task benchmark.");

        await using var server = await StartSiteAsync(app =>
        {
            app.MapGet("/", () => Results.Content("""
                <!doctype html><html><head><title>Home</title></head>
                <body><h1>Welcome</h1><p><a href="/details">See details</a></p></body></html>
                """, "text/html"));
            app.MapGet("/details", () => Results.Content("""
                <!doctype html><html><head><title>Details</title></head>
                <body><h1>Details</h1><p id="secret">SECRET-TOKEN-42</p></body></html>
                """, "text/html"));
        });

        var capability = BuildCapability(config!);
        var request = Request("""
            {"intent":"Starting from the home page, click the See details link and report the secret token shown on the destination page as the result.",
             "url":"%URL%/",
             "allowedHosts":["127.0.0.1"],
             "maxSteps":20}
            """.Replace("%URL%", server.BaseUrl));

        var result = await capability.ExecuteAsync(request, BuildContext());
        var payload = ParsePayload(result);

        Assert.Equal("done", payload.Status);
        Assert.Contains("SECRET-TOKEN-42", payload.Result ?? payload.Summary ?? string.Empty);
    }

    [SkippableFact]
    public async Task FormSubmit_ReportsConfirmationMessage()
    {
        var config = LlmConfig.FromEnvironment();
        Skip.If(config is null, "FORAGENT_LLM_* env vars not set — skipping browser-task benchmark.");

        await using var server = await StartSiteAsync(app =>
        {
            app.MapGet("/form", () => Results.Content("""
                <!doctype html><html><head><title>Contact</title></head>
                <body>
                  <h1>Contact us</h1>
                  <form method="post" action="/submit">
                    <label>Name <input name="name" type="text"></label>
                    <label>Message <textarea name="message"></textarea></label>
                    <button type="submit">Send</button>
                  </form>
                </body></html>
                """, "text/html"));
            app.MapPost("/submit", async (HttpRequest r) =>
            {
                var form = await r.ReadFormAsync();
                var name = form["name"].ToString();
                return Results.Content($"""
                    <!doctype html><html><head><title>Thanks</title></head>
                    <body><h1 id="confirm">Thanks {name}, we received your message.</h1></body></html>
                    """, "text/html");
            });
        });

        var capability = BuildCapability(config!);
        var request = Request("""
            {"intent":"Fill the contact form with name 'Rocky' and message 'Hello from step 6', submit it, and report the confirmation headline you see next.",
             "url":"%URL%/form",
             "allowedHosts":["127.0.0.1"],
             "maxSteps":20}
            """.Replace("%URL%", server.BaseUrl));

        var result = await capability.ExecuteAsync(request, BuildContext());
        var payload = ParsePayload(result);

        Assert.Equal("done", payload.Status);
        var combined = (payload.Result ?? string.Empty) + " " + (payload.Summary ?? string.Empty);
        Assert.Contains("Rocky", combined);
        Assert.Contains("received your message", combined, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task MultiPageNav_ReadsNestedContent()
    {
        var config = LlmConfig.FromEnvironment();
        Skip.If(config is null, "FORAGENT_LLM_* env vars not set — skipping browser-task benchmark.");

        await using var server = await StartSiteAsync(app =>
        {
            app.MapGet("/", () => Results.Content("""
                <!doctype html><html><head><title>Docs index</title></head>
                <body><h1>Docs</h1><ul>
                <li><a href="/intro">Intro</a></li>
                <li><a href="/advanced">Advanced</a></li>
                </ul></body></html>
                """, "text/html"));
            app.MapGet("/intro", () => Results.Content("""
                <!doctype html><html><head><title>Intro</title></head>
                <body><h1>Intro</h1><p><a href="/intro/chapter-2">Chapter 2</a></p></body></html>
                """, "text/html"));
            app.MapGet("/intro/chapter-2", () => Results.Content("""
                <!doctype html><html><head><title>Chapter 2</title></head>
                <body><h1>Chapter 2: the widget</h1>
                <p>The answer you seek is <strong id="answer">FORTY-TWO</strong>.</p></body></html>
                """, "text/html"));
            app.MapGet("/advanced", () => Results.Content("""
                <!doctype html><html><body>No answer here.</body></html>
                """, "text/html"));
        });

        var capability = BuildCapability(config!);
        var request = Request("""
            {"intent":"Starting from the docs index, navigate into Intro and then into its Chapter 2, and return the strong-emphasised answer word you find there.",
             "url":"%URL%/",
             "allowedHosts":["127.0.0.1"],
             "maxSteps":30}
            """.Replace("%URL%", server.BaseUrl));

        var result = await capability.ExecuteAsync(request, BuildContext());
        var payload = ParsePayload(result);

        Assert.Equal("done", payload.Status);
        Assert.Contains("FORTY-TWO", (payload.Result ?? string.Empty) + (payload.Summary ?? string.Empty));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private BrowserTaskCapability BuildCapability(LlmConfig config)
    {
        var openAi = new OpenAIClient(
            new ApiKeyCredential(config.ApiKey),
            new OpenAIClientOptions { Endpoint = new Uri(config.Endpoint) });
        var inner = openAi.GetChatClient(config.ModelId).AsIChatClient();

        // Match the production wiring — the capability sees a
        // function-invoking IChatClient (same shape as what
        // AddRockBotTieredChatClients installs in Program.cs).
        var chatClient = new ChatClientBuilder(inner)
            .UseFunctionInvocation()
            .Build();

        var skillStore = new NoopSkillStore();
        var memory = new NoopLongTermMemory();
        var priming = new BrowserTaskPriming(
            skillStore,
            memory,
            embeddingGenerator: null,
            NullLogger<BrowserTaskPriming>.Instance);

        return new BrowserTaskCapability(
            fixture.Factory,
            chatClient,
            new NoCredentialsBroker(),
            priming,
            skillStore,
            NullLogger<BrowserTaskCapability>.Instance);
    }

    private sealed class NoopSkillStore : ISkillStore
    {
        public Task SaveAsync(Skill skill) => Task.CompletedTask;
        public Task<Skill?> GetAsync(string name) => Task.FromResult<Skill?>(null);
        public Task<IReadOnlyList<Skill>> ListAsync() =>
            Task.FromResult<IReadOnlyList<Skill>>([]);
        public Task DeleteAsync(string name) => Task.CompletedTask;
        public Task<IReadOnlyList<Skill>> SearchAsync(
            string query, int maxResults, CancellationToken cancellationToken = default, float[]? queryEmbedding = null) =>
            Task.FromResult<IReadOnlyList<Skill>>([]);
    }

    private sealed class NoopLongTermMemory : ILongTermMemory
    {
        public Task SaveAsync(MemoryEntry entry, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<MemoryEntry>> SearchAsync(MemorySearchCriteria criteria, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MemoryEntry>>([]);
        public Task<MemoryEntry?> GetAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult<MemoryEntry?>(null);
        public Task DeleteAsync(string id, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListTagsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<string>> ListCategoriesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private static AgentTaskRequest Request(string json) => new()
    {
        TaskId = Guid.NewGuid().ToString(),
        Skill = BrowserTaskCapability.SkillDefinition.Id,
        Message = new AgentMessage
        {
            Role = "user",
            Parts = [new AgentMessagePart { Kind = "text", Text = json }]
        }
    };

    private static AgentTaskContext BuildContext()
    {
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
        return new AgentTaskContext
        {
            MessageContext = messageContext,
            PublishStatus = (_, _) => Task.CompletedTask
        };
    }

    private static TaskPayload ParsePayload(AgentTaskResult result)
    {
        var text = result.Message?.Parts.FirstOrDefault(p => p.Kind == "text")?.Text ?? "{}";
        using var doc = JsonDocument.Parse(text);
        var r = doc.RootElement;
        return new TaskPayload(
            Status: r.TryGetProperty("status", out var s) ? s.GetString() : null,
            Summary: r.TryGetProperty("summary", out var sm) ? sm.GetString() : null,
            Result: r.TryGetProperty("result", out var rs) && rs.ValueKind != JsonValueKind.Null
                ? rs.GetString() : null);
    }

    private static async Task<SiteHost> StartSiteAsync(Action<WebApplication> configure)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddRoutingCore();
        builder.Logging.ClearProviders();

        var app = builder.Build();
        app.UseRouting();
        configure(app);
        await app.StartAsync();
        var addresses = app.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
            .Addresses;
        return new SiteHost(app, addresses.First().TrimEnd('/'));
    }

    private sealed record TaskPayload(string? Status, string? Summary, string? Result);

    private sealed record SiteHost(WebApplication App, string BaseUrl) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    private sealed record LlmConfig(string Endpoint, string ModelId, string ApiKey)
    {
        public static LlmConfig? FromEnvironment()
        {
            var endpoint = Environment.GetEnvironmentVariable("FORAGENT_LLM_ENDPOINT");
            var model = Environment.GetEnvironmentVariable("FORAGENT_LLM_MODEL_ID");
            var key = Environment.GetEnvironmentVariable("FORAGENT_LLM_API_KEY");
            if (string.IsNullOrWhiteSpace(endpoint)
                || string.IsNullOrWhiteSpace(model)
                || string.IsNullOrWhiteSpace(key))
            {
                return null;
            }
            return new LlmConfig(endpoint, model, key);
        }
    }

    private sealed class NoCredentialsBroker : ICredentialBroker
    {
        public Task<CredentialReference> ResolveAsync(string credentialId, CancellationToken cancellationToken = default) =>
            throw new CredentialNotFoundException(credentialId);
    }
}
