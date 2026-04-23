using System.ClientModel;
using Foragent.Agent;
using Foragent.Browser;
using Foragent.Capabilities;
using Foragent.Credentials;
using Microsoft.Extensions.AI;
using OpenAI;
using RockBot.A2A;
using RockBot.A2A.Gateway;
using RockBot.A2A.Gateway.Auth;
using RockBot.Host;
using RockBot.Llm;
using RockBot.Messaging.RabbitMQ;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddUserSecrets<Program>(optional: true);

// ── LLM (required — capabilities like extract-structured-data reason over
//    page content via Microsoft.Extensions.AI). Config is namespaced under
//    ForagentLlm so it can differ from the host's RockBot LLM config. ───────

var llmSection = builder.Configuration.GetSection("ForagentLlm");
var llmEndpoint = llmSection["Endpoint"]
    ?? throw new InvalidOperationException("ForagentLlm:Endpoint is required. Set FORAGENT_LLM_ENDPOINT.");
var llmModelId = llmSection["ModelId"]
    ?? throw new InvalidOperationException("ForagentLlm:ModelId is required. Set FORAGENT_LLM_MODEL_ID.");
var llmApiKey = llmSection["ApiKey"]
    ?? throw new InvalidOperationException("ForagentLlm:ApiKey is required. Set FORAGENT_LLM_API_KEY.");

var openAiClient = new OpenAIClient(
    new ApiKeyCredential(llmApiKey),
    new OpenAIClientOptions { Endpoint = new Uri(llmEndpoint) });
var foragentChatClient = openAiClient.GetChatClient(llmModelId).AsIChatClient();

// ── Embeddings (optional, spec §5.6). Separate config section because
//    embedding deployments often live on a different Azure AI Foundry
//    subscription than chat completions. Missing config is logged once and
//    downgrades skill/memory retrieval to BM25-only. ────────────────────────
var embedSection = builder.Configuration.GetSection("ForagentEmbeddings");
var embedEndpoint = embedSection["Endpoint"];
var embedModelId = embedSection["ModelId"];
var embedApiKey = embedSection["ApiKey"];
var embeddingsConfigured = !string.IsNullOrWhiteSpace(embedEndpoint)
    && !string.IsNullOrWhiteSpace(embedModelId)
    && !string.IsNullOrWhiteSpace(embedApiKey);
if (embeddingsConfigured)
{
    var embeddingClient = new OpenAIClient(
        new ApiKeyCredential(embedApiKey!),
        new OpenAIClientOptions { Endpoint = new Uri(embedEndpoint!) })
        .GetEmbeddingClient(embedModelId!)
        .AsIEmbeddingGenerator();
    builder.Services.AddSingleton(embeddingClient);
}

// ── Messaging (RabbitMQ) ─────────────────────────────────────────────────────

builder.Services.AddRockBotRabbitMq(opts =>
    builder.Configuration.GetSection("RabbitMq").Bind(opts));

// ── Tiered chat clients (spec §3.7, Appendix #17). One configured model is
//    aliased across Low/Balanced/High; capabilities that inject IChatClient
//    receive the Balanced tier. Tier-aware capabilities (browser-task) may
//    resolve TieredChatClientRegistry to escalate/de-escalate. The factory
//    inside AddRockBotTieredChatClients already wraps with
//    RockBotFunctionInvokingChatClient — AddRockBotChatClient is redundant
//    once this is called.
builder.Services.AddRockBotTieredChatClients(
    lowInnerClient: foragentChatClient,
    balancedInnerClient: foragentChatClient,
    highInnerClient: foragentChatClient);

// ── Agent host + A2A bus subscription ───────────────────────────────────────

var gatewaySection = builder.Configuration.GetSection("Gateway");
var agentName = gatewaySection["InternalAgentName"] ?? gatewaySection["AgentName"] ?? "Foragent";

// Skill + long-term memory paths. File-backed stores from RockBot.Host; both
// directories are created on first write. docker-compose mounts a named volume
// at these paths so learned site knowledge survives container restarts.
var memorySection = builder.Configuration.GetSection("ForagentMemory");
var skillsPath = memorySection["SkillsPath"] ?? "data/skills";
var memoryPath = memorySection["MemoryPath"] ?? "data/memory";

builder.Services.AddRockBotHost(agent =>
{
    agent.WithIdentity(agentName);

    agent.AddA2A(opts =>
    {
        opts.Card = new AgentCard
        {
            AgentName = gatewaySection["AgentName"] ?? "Foragent",
            Description = gatewaySection["Description"]
                ?? "Browser agent exposing task-level web automation over A2A.",
            Version = gatewaySection["Version"] ?? "1.0",
            Skills = [.. ForagentCapabilities.Skills]
        };
    });

    // Step 7: skills + long-term memory as the learning substrate (spec §5.6).
    // FileSkillStore / FileMemoryStore pick up an IEmbeddingGenerator from DI
    // when registered (see ForagentEmbeddings above); otherwise they fall back
    // to BM25 retrieval.
    agent.WithSkills(opts => opts.BasePath = skillsPath);
    agent.WithLongTermMemory(opts => opts.BasePath = memoryPath);

    agent.Services.AddForagentCapabilities();
    agent.Services.AddHostedService<BskySeedSkillService>();
});

builder.Services.AddForagentBrowser();

// ── Credentials ─────────────────────────────────────────────────────────────
// In-memory broker bound to the "Credentials" config section (populated via
// user-secrets in dev). Production deployments should swap in a k8s-secrets /
// vault broker; tracked in docs/framework-feedback.md step 4.
builder.Services.AddForagentCredentials(builder.Configuration);

// ── HTTP A2A gateway (in-process) ────────────────────────────────────────────

builder.Services.Configure<Dictionary<string, ApiKeyEntry>>(
    builder.Configuration.GetSection("ApiKeys"));

builder.Services.AddA2AApiKeyAuthentication();
builder.Services.AddA2AHttpGateway(opts =>
{
    gatewaySection.Bind(opts);
    // Agent card for HTTP discovery is sourced from the registered capabilities,
    // not from appsettings — avoids maintaining two copies of the skill list.
    opts.Skills = [.. ForagentCapabilities.Skills
        .Select(s => new GatewaySkillConfig { Id = s.Id, Name = s.Name, Description = s.Description })];
});

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.MapA2AHttpGateway();
app.MapGet("/health", () => Results.Ok("ok"));

app.Logger.LogInformation(
    "Foragent starting — HTTP A2A on {Urls}, bus identity '{Identity}'",
    string.Join(", ", app.Urls.DefaultIfEmpty("(default)")), agentName);

if (embeddingsConfigured)
{
    app.Logger.LogInformation(
        "ForagentEmbeddings configured ({ModelId}); skill + memory retrieval will use hybrid BM25 + vector.",
        embedModelId);
}
else
{
    app.Logger.LogWarning(
        "ForagentEmbeddings not configured — skill + memory retrieval will use BM25 only. "
        + "Set ForagentEmbeddings:Endpoint/ModelId/ApiKey to enable semantic retrieval.");
}

app.Run();

public partial class Program;
