using System.Text.Json;
using Foragent.Capabilities.Forms;
using Foragent.Credentials;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.A2A;
using RockBot.Host;
using RockBot.Messaging;
using Xunit;

namespace Foragent.Browser.Tests;

/// <summary>
/// Step 8 end-to-end: drive <c>learn-form-schema</c> + <c>execute-form-batch</c>
/// against a real Kestrel-hosted form with real Chromium. No LLM required —
/// the form has no select/radio fields, so the <see cref="FormSchemaEnricher"/>
/// short-circuits without calling a chat client.
/// </summary>
[Collection("Playwright")]
public class FormCapabilitiesIntegrationTests(TestPageServerFixture fixture)
{
    [Fact]
    public async Task LearnThenExecute_SubmitsRowsAgainstRealForm()
    {
        var submissions = new List<string>();
        await using var server = await StartSiteAsync(app =>
        {
            app.MapGet("/contact", () => Results.Content("""
                <!doctype html><html><head><title>Contact</title></head>
                <body>
                  <h1>Contact us</h1>
                  <form id="contact" method="post" action="/submit">
                    <label for="email-input">Email</label>
                    <input id="email-input" name="email" type="email" required maxlength="120">
                    <label for="msg">Message</label>
                    <textarea id="msg" name="message" maxlength="1000"></textarea>
                    <button id="submit" type="submit">Send</button>
                  </form>
                </body></html>
                """, "text/html"));
            app.MapPost("/submit", async (HttpContext ctx) =>
            {
                var form = await ctx.Request.ReadFormAsync();
                submissions.Add($"{form["email"]}|{form["message"]}");
                return Results.Content("""
                    <!doctype html><html><head><title>Thanks</title></head>
                    <body><p class="thanks">Message received.</p></body></html>
                    """, "text/html");
            });
        });

        var skills = new InMemorySkillStore();
        var learn = BuildLearn(skills);

        var learnResult = await learn.ExecuteAsync(
            FormRequest("learn-form-schema", $$"""
                {
                  "url": "{{server.BaseUrl}}/contact",
                  "allowedHosts": ["127.0.0.1"]
                }
                """),
            BuildContext());

        Assert.Equal(AgentTaskState.Completed, learnResult.State);
        using var learnDoc = JsonDocument.Parse(TextOf(learnResult));
        Assert.Equal("done", learnDoc.RootElement.GetProperty("status").GetString());
        var skillName = learnDoc.RootElement.GetProperty("skillName").GetString()!;
        Assert.True(skills.HasResource(skillName, "schema.json"));

        // Schema round-trips to the shape execute-form-batch expects.
        var resourceJson = skills.GetResource(skillName, "schema.json")!;
        var schema = FormSchema.Deserialize(resourceJson);
        Assert.Contains(schema.Fields, f => f.Name == "email" && f.Type == FormFieldType.Email && f.Required);
        Assert.Contains(schema.Fields, f => f.Name == "message" && f.Type == FormFieldType.TextArea);
        Assert.Equal("#submit", schema.SubmitSelector);

        var execute = BuildExecute(skills);
        var executeResult = await execute.ExecuteAsync(
            FormRequest("execute-form-batch", $$"""
                {
                  "schemaRef": "{{skillName}}",
                  "allowedHosts": ["127.0.0.1"],
                  "successIndicator": ".thanks",
                  "rows": [
                    {"email":"a@example.com","message":"hello"},
                    {"email":"b@example.com","message":"world"}
                  ]
                }
                """),
            BuildContext());

        Assert.Equal(AgentTaskState.Completed, executeResult.State);
        using var execDoc = JsonDocument.Parse(TextOf(executeResult));
        Assert.Equal("done", execDoc.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, execDoc.RootElement.GetProperty("successCount").GetInt32());
        Assert.Equal(2, submissions.Count);
        Assert.Equal("a@example.com|hello", submissions[0]);
        Assert.Equal("b@example.com|world", submissions[1]);
    }

    private LearnFormSchemaCapability BuildLearn(InMemorySkillStore skills)
    {
        var enricher = new FormSchemaEnricher(
            new UnreachableChatClient(), NullLogger<FormSchemaEnricher>.Instance);
        return new LearnFormSchemaCapability(
            fixture.Factory,
            new NoCredentialBroker(),
            enricher,
            skills,
            NullLogger<LearnFormSchemaCapability>.Instance);
    }

    private ExecuteFormBatchCapability BuildExecute(InMemorySkillStore skills) =>
        new(
            fixture.Factory,
            new NoCredentialBroker(),
            skills,
            NullLogger<ExecuteFormBatchCapability>.Instance);

    private static AgentTaskRequest FormRequest(string skill, string json) => new()
    {
        TaskId = Guid.NewGuid().ToString(),
        Skill = skill,
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
        return new AgentTaskContext
        {
            MessageContext = new MessageHandlerContext
            {
                Envelope = envelope,
                Agent = new AgentIdentity("Foragent"),
                Services = new ServiceCollection().BuildServiceProvider(),
                CancellationToken = CancellationToken.None
            },
            PublishStatus = (_, _) => Task.CompletedTask
        };
    }

    private static string TextOf(AgentTaskResult result) =>
        result.Message?.Parts.FirstOrDefault(p => p.Kind == "text")?.Text ?? string.Empty;

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

    private sealed record SiteHost(WebApplication App, string BaseUrl) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    private sealed class NoCredentialBroker : ICredentialBroker
    {
        public Task<CredentialReference> ResolveAsync(string id, CancellationToken ct = default) =>
            throw new CredentialNotFoundException(id);
    }

    private sealed class InMemorySkillStore : ISkillStore
    {
        private readonly Dictionary<string, Skill> _skills = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, string>> _resources = new(StringComparer.Ordinal);

        public Task SaveAsync(Skill skill)
        {
            _skills[skill.Name] = skill;
            return Task.CompletedTask;
        }

        public Task SaveAsync(Skill skill, IReadOnlyList<SkillResourceInput>? resources)
        {
            if (resources is null || resources.Count == 0)
            {
                _skills[skill.Name] = skill;
                return Task.CompletedTask;
            }
            var manifest = resources.Select(r => new SkillResource(r.Filename, r.Type, r.Description)).ToList();
            _skills[skill.Name] = skill with { Manifest = manifest };
            _resources[skill.Name] = resources.ToDictionary(r => r.Filename, r => r.Content, StringComparer.Ordinal);
            return Task.CompletedTask;
        }

        public Task<string?> GetResourceAsync(string skillName, string filename) =>
            Task.FromResult(_resources.TryGetValue(skillName, out var bundle) && bundle.TryGetValue(filename, out var c) ? c : null);

        public Task<Skill?> GetAsync(string name) =>
            Task.FromResult(_skills.TryGetValue(name, out var s) ? s : null);

        public Task<IReadOnlyList<Skill>> ListAsync() =>
            Task.FromResult<IReadOnlyList<Skill>>([.. _skills.Values]);

        public Task DeleteAsync(string name)
        {
            _skills.Remove(name);
            _resources.Remove(name);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Skill>> SearchAsync(
            string query, int maxResults, CancellationToken cancellationToken = default, float[]? queryEmbedding = null) =>
            Task.FromResult<IReadOnlyList<Skill>>([]);

        public bool HasResource(string skillName, string filename) =>
            _resources.TryGetValue(skillName, out var bundle) && bundle.ContainsKey(filename);

        public string? GetResource(string skillName, string filename) =>
            _resources.TryGetValue(skillName, out var bundle) && bundle.TryGetValue(filename, out var c) ? c : null;
    }

    private sealed class UnreachableChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "The enricher should not call an LLM for forms without select/radio fields.");

#pragma warning disable CS1998
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
}
