using System.ClientModel;
using System.Text.Json;
using Foragent.Capabilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI;
using RockBot.A2A;
using RockBot.Host;
using RockBot.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Foragent.Browser.Tests;

/// <summary>
/// End-to-end test that runs the real <see cref="ExtractStructuredDataCapability"/>
/// against a local Kestrel page, driving real Playwright Chromium and a real
/// LLM. Skipped automatically when <c>FORAGENT_LLM_*</c> env vars aren't set —
/// so the unit + browser test suites still run in CI without credentials.
/// </summary>
[Collection("Playwright")]
public class ExtractStructuredDataIntegrationTests(TestPageServerFixture fixture)
{
    [SkippableFact]
    public async Task ExtractsProductPrice_FromFakeShopPage()
    {
        var config = LlmConfig.FromEnvironment();
        Skip.If(config is null, "FORAGENT_LLM_* env vars not set — skipping real-LLM test.");

        await using var shop = await StartFakeShopAsync();
        var capability = BuildCapability(config!);
        var request = BuildRequest(
            $"{shop.BaseUrl}/product",
            "the product name and price as fields called 'name' and 'price_usd'");

        var result = await capability.ExecuteAsync(request, BuildContext());

        Assert.Equal(AgentTaskState.Completed, result.State);
        var text = result.Message?.Parts.FirstOrDefault(p => p.Kind == "text")?.Text ?? string.Empty;

        using var doc = JsonDocument.Parse(text);
        var name = doc.RootElement.GetProperty("name").GetString();
        Assert.Equal("Premium Widget", name, ignoreCase: true);

        // price_usd may come back as a number or string — accept either.
        var priceElement = doc.RootElement.GetProperty("price_usd");
        var price = priceElement.ValueKind == JsonValueKind.Number
            ? priceElement.GetDecimal()
            : decimal.Parse(priceElement.GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(49.99m, price);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private ExtractStructuredDataCapability BuildCapability(LlmConfig config)
    {
        var openAi = new OpenAIClient(
            new ApiKeyCredential(config.ApiKey),
            new OpenAIClientOptions { Endpoint = new Uri(config.Endpoint) });
        var chatClient = openAi.GetChatClient(config.ModelId).AsIChatClient();
        return new ExtractStructuredDataCapability(
            fixture.Factory, chatClient, NullLogger<ExtractStructuredDataCapability>.Instance);
    }

    private static AgentTaskRequest BuildRequest(string url, string description) => new()
    {
        TaskId = Guid.NewGuid().ToString(),
        Skill = ExtractStructuredDataCapability.SkillDefinition.Id,
        Message = new AgentMessage
        {
            Role = "user",
            Parts =
            [
                new AgentMessagePart
                {
                    Kind = "text",
                    Text = JsonSerializer.Serialize(new { url, description })
                }
            ]
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

    private static async Task<FakeShop> StartFakeShopAsync()
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddRoutingCore();
        builder.Logging.ClearProviders();

        var app = builder.Build();
        app.UseRouting();
        app.MapGet("/product", () => Results.Content("""
            <!doctype html>
            <html>
              <head><title>Premium Widget | The Shop</title></head>
              <body>
                <h1>Premium Widget</h1>
                <p class="description">A top-of-the-line widget for demanding applications.</p>
                <p class="price">USD 49.99</p>
                <button>Buy now</button>
              </body>
            </html>
            """, "text/html"));

        await app.StartAsync();
        var addresses = app.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
            .Addresses;
        return new FakeShop(app, addresses.First().TrimEnd('/'));
    }

    private sealed record FakeShop(WebApplication App, string BaseUrl) : IAsyncDisposable
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
}
