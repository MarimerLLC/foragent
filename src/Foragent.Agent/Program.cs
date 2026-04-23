using System.ClientModel;
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

    agent.Services.AddForagentCapabilities();
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

app.Run();

public partial class Program;
