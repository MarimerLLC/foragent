using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Foragent.Browser;

public static class ForagentBrowserServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="PlaywrightBrowserHost"/> as a singleton hosted
    /// service plus an <see cref="IBrowserSessionFactory"/> that hands out
    /// fresh isolated browser contexts from the shared browser.
    /// </summary>
    public static IServiceCollection AddForagentBrowser(this IServiceCollection services)
    {
        services.AddSingleton<PlaywrightBrowserHost>();
        services.AddHostedService(sp => sp.GetRequiredService<PlaywrightBrowserHost>());
        services.AddSingleton<IBrowserSessionFactory, PlaywrightBrowserSessionFactory>();
        return services;
    }
}
