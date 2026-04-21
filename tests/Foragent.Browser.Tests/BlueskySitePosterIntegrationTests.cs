using Foragent.Capabilities.SitePosting;
using Foragent.Credentials;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Foragent.Browser.Tests;

/// <summary>
/// Drives the real <see cref="BlueskySitePoster"/> against a Kestrel-hosted
/// fake bsky.app-shaped login + compose UI. Validates the full login → post
/// → confirm flow through a real Chromium. The fake server mirrors the
/// selectors the poster targets (sign-in button, identifier / password
/// placeholders, compose contenteditable, post button, home feed heading).
/// </summary>
[Collection("Playwright")]
public class BlueskySitePosterIntegrationTests(TestPageServerFixture fixture)
{
    [Fact]
    public async Task Posts_AfterLogin_OnHappyPath()
    {
        await using var fake = await FakeBlueskyServer.StartAsync(
            expectedIdentifier: "rocky.bsky.social",
            expectedPassword: "app-pass-xyz");

        var poster = new BlueskySitePoster(
            NullLogger<BlueskySitePoster>.Instance,
            new Uri(fake.BaseUrl + "/"));
        var credential = new CredentialReference(
            "rockbot/social/bluesky-rocky",
            "username-password",
            new Dictionary<string, string>
            {
                ["identifier"] = "rocky.bsky.social",
                ["password"] = "app-pass-xyz"
            });

        await using var session = await fixture.Factory.CreateSessionAsync();
        await poster.PostAsync(session, credential, "hello from Foragent integration test", CancellationToken.None);

        Assert.Equal("hello from Foragent integration test", fake.LastPostedContent);
        Assert.Equal(1, fake.SuccessfulLogins);
    }

    [Fact]
    public async Task Throws_WhenCredentialFieldMissing()
    {
        var poster = new BlueskySitePoster(
            NullLogger<BlueskySitePoster>.Instance,
            new Uri("http://127.0.0.1/"));
        var credential = new CredentialReference(
            "id", "username-password",
            new Dictionary<string, string> { ["identifier"] = "u" });

        await using var session = await fixture.Factory.CreateSessionAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            poster.PostAsync(session, credential, "hi", CancellationToken.None));

        Assert.Contains("password", ex.Message);
    }
}

/// <summary>
/// A minimal HTML server that shapes like the Bluesky web UI enough for
/// <see cref="BlueskySitePoster"/> to drive. Hand-rolled HTML keeps the test
/// deterministic — no JS frameworks, no network, no external state.
/// </summary>
internal sealed class FakeBlueskyServer : IAsyncDisposable
{
    private const string SessionCookieName = "fake_bsky_session";

    private readonly WebApplication _app;
    private readonly string _expectedIdentifier;
    private readonly string _expectedPassword;

    public string BaseUrl { get; }
    public string? LastPostedContent { get; private set; }
    public int SuccessfulLogins { get; private set; }

    private FakeBlueskyServer(WebApplication app, string baseUrl, string identifier, string password)
    {
        _app = app;
        BaseUrl = baseUrl;
        _expectedIdentifier = identifier;
        _expectedPassword = password;
    }

    public static async Task<FakeBlueskyServer> StartAsync(string expectedIdentifier, string expectedPassword)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddRoutingCore();
        builder.Logging.ClearProviders();

        var app = builder.Build();
        app.UseRouting();

        // Built first so handlers can close over the instance and write state
        // directly. Routes are registered below.
        FakeBlueskyServer? fake = null;

        app.MapGet("/", () => Results.Content(Landing(), "text/html"));

        app.MapGet("/login", () => Results.Content(LoginForm(), "text/html"));

        app.MapPost("/login", async (HttpContext ctx) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            var id = form["identifier"].ToString();
            var pw = form["password"].ToString();
            if (id != fake!._expectedIdentifier || pw != fake._expectedPassword)
                return Results.Content(LoginForm(error: "Invalid credentials"), "text/html");

            fake.SuccessfulLogins++;
            ctx.Response.Cookies.Append(SessionCookieName, "ok");
            return Results.Redirect("/home");
        });

        app.MapGet("/home", (HttpContext ctx) =>
        {
            if (ctx.Request.Cookies[SessionCookieName] != "ok")
                return Results.Redirect("/login");
            return Results.Content(Home(), "text/html");
        });

        app.MapGet("/compose", (HttpContext ctx) =>
        {
            if (ctx.Request.Cookies[SessionCookieName] != "ok")
                return Results.Redirect("/login");
            return Results.Content(Compose(), "text/html");
        });

        app.MapPost("/compose", async (HttpContext ctx) =>
        {
            if (ctx.Request.Cookies[SessionCookieName] != "ok")
                return Results.Redirect("/login");
            var form = await ctx.Request.ReadFormAsync();
            fake!.LastPostedContent = form["content"].ToString();
            return Results.Redirect("/home");
        });

        await app.StartAsync();
        var server = app.Services.GetRequiredService<IServer>();
        var baseUrl = server.Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');

        fake = new FakeBlueskyServer(app, baseUrl, expectedIdentifier, expectedPassword);
        return fake;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    // ── HTML fragments — minimal but shaped for the poster's selectors ──────

    private static string Landing() => """
        <!doctype html><html><head><title>Bluesky</title></head>
        <body>
          <h1>Welcome</h1>
          <a href="/login" role="button">Sign in</a>
        </body></html>
        """;

    private static string LoginForm(string? error = null) => $$"""
        <!doctype html><html><head><title>Sign in</title></head>
        <body>
          <form method="post" action="/login">
            <input name="identifier" placeholder="Username or email address" />
            <input name="password" type="password" placeholder="Password" />
            <button type="submit">Next</button>
          </form>
          {{(error is null ? "" : $"<div role='alert'>{error}</div>")}}
        </body></html>
        """;

    private static string Home() => """
        <!doctype html><html><head><title>Home - Bluesky</title></head>
        <body>
          <h2>Home</h2>
          <a href="/compose" role="button">New post</a>
        </body></html>
        """;

    private static string Compose() => """
        <!doctype html><html><head><title>Compose - Bluesky</title></head>
        <body>
          <form method="post" action="/compose" id="compose-form">
            <div contenteditable="true" id="editor" aria-label="Post content"></div>
            <input type="hidden" name="content" id="content-hidden" />
            <button type="button" id="post-btn" role="button">Post</button>
          </form>
          <script>
            document.getElementById('post-btn').addEventListener('click', () => {
              document.getElementById('content-hidden').value =
                document.getElementById('editor').innerText;
              document.getElementById('compose-form').submit();
            });
          </script>
        </body></html>
        """;
}
