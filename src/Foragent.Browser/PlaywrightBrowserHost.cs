using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace Foragent.Browser;

/// <summary>
/// Owns the process-wide Playwright + Chromium browser lifecycle. Launches on
/// <see cref="IHostedService.StartAsync"/>, disposes on shutdown. Registered
/// as a singleton so <see cref="PlaywrightBrowserSessionFactory"/> can reuse
/// the same browser for every session (spec §3.5).
/// </summary>
public sealed class PlaywrightBrowserHost(
    ILogger<PlaywrightBrowserHost> logger) : IHostedService, IAsyncDisposable
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public IBrowser Browser => _browser
        ?? throw new InvalidOperationException(
            "Browser is not started. PlaywrightBrowserHost must be added as an IHostedService and the host must be running.");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Launching Playwright Chromium");
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
        logger.LogInformation("Chromium launched (version {Version})", _browser.Version);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.CloseAsync();
            _browser = null;
        }
        _playwright?.Dispose();
        _playwright = null;
    }
}
