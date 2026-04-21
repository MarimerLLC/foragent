using Foragent.Agent;
using Foragent.Browser;
using Foragent.Capabilities;
using Microsoft.Extensions.AI;
using RockBot.A2A;
using RockBot.A2A.Gateway;
using RockBot.A2A.Gateway.Auth;
using RockBot.Host;
using RockBot.Messaging.RabbitMQ;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddUserSecrets<Program>(optional: true);

// ── Messaging (RabbitMQ) ─────────────────────────────────────────────────────

builder.Services.AddRockBotRabbitMq(opts =>
    builder.Configuration.GetSection("RabbitMq").Bind(opts));

// ── Chat client — Foragent v1 does no LLM reasoning, but the framework requires
//    IChatClient to be registered. EchoChatClient is inert. ────────────────────

builder.Services.AddRockBotChatClient(new EchoChatClient());

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
            Skills =
            [
                new AgentSkill
                {
                    Id = ForagentTaskHandler.FetchPageTitleSkillId,
                    Name = "Fetch Page Title",
                    Description = "Fetches a URL and returns the contents of its <title> element."
                }
            ]
        };
    });

    agent.Services.AddScoped<IAgentTaskHandler, ForagentTaskHandler>();
});

builder.Services.AddForagentBrowser();

// ── HTTP A2A gateway (in-process) ────────────────────────────────────────────

builder.Services.Configure<Dictionary<string, ApiKeyEntry>>(
    builder.Configuration.GetSection("ApiKeys"));

builder.Services.AddA2AApiKeyAuthentication();
builder.Services.AddA2AHttpGateway(opts => gatewaySection.Bind(opts));

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
