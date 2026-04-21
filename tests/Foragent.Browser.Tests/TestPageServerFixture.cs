using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Foragent.Browser.Tests;

/// <summary>
/// xUnit collection fixture that stands up a Kestrel host serving canned pages
/// plus a shared <see cref="PlaywrightBrowserHost"/>. Tests get fresh
/// <see cref="IBrowserSession"/> instances from the real factory, so they
/// exercise the same code path the agent uses in production. Chromium binaries
/// are installed on first run (cached under ~/.cache/ms-playwright).
/// </summary>
public sealed class TestPageServerFixture : IAsyncLifetime
{
    private WebApplication? _app;
    private PlaywrightBrowserHost? _browserHost;

    public IBrowserSessionFactory Factory { get; private set; } = null!;
    public string BaseUrl { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        EnsureChromiumInstalled();

        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddRoutingCore();
        builder.Logging.ClearProviders();

        _app = builder.Build();
        _app.UseRouting();

        _app.MapGet("/plain", () => Results.Content(
            "<html><head><title>Hello World</title></head><body>hi</body></html>", "text/html"));

        _app.MapGet("/no-title", () => Results.Content(
            "<html><body>no head here</body></html>", "text/html"));

        _app.MapGet("/entities", () => Results.Content(
            "<html><head><title>AT&amp;T &mdash; Home</title></head></html>", "text/html"));

        _app.MapGet("/js-title", () => Results.Content(
            """<!doctype html><html><head><title>initial</title><script>document.title='updated by js';</script></head><body></body></html>""",
            "text/html"));

        _app.MapGet("/redirect", () => Results.Redirect("/plain"));

        _app.MapGet("/not-found", () => Results.NotFound());

        await _app.StartAsync();
        var server = _app.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()!.Addresses;
        BaseUrl = addresses.First().TrimEnd('/');

        _browserHost = new PlaywrightBrowserHost(NullLogger<PlaywrightBrowserHost>.Instance);
        await _browserHost.StartAsync(CancellationToken.None);
        Factory = new PlaywrightBrowserSessionFactory(_browserHost);
    }

    public async Task DisposeAsync()
    {
        if (_browserHost is not null) await _browserHost.DisposeAsync();
        if (_app is not null) await _app.DisposeAsync();
    }

    public Uri Url(string path) => new($"{BaseUrl}{path}");

    private static void EnsureChromiumInstalled()
    {
        var exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
        if (exitCode != 0)
            throw new InvalidOperationException(
                $"Playwright Chromium install failed with exit code {exitCode}.");
    }
}

[CollectionDefinition("Playwright")]
public sealed class PlaywrightCollection : ICollectionFixture<TestPageServerFixture>
{
}
