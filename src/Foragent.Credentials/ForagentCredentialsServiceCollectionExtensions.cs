using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Foragent.Credentials;

public static class ForagentCredentialsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the in-memory credential broker as the default
    /// <see cref="ICredentialBroker"/>, bound to the supplied config section
    /// (commonly <c>Credentials</c>). Production deployments should replace
    /// this with a k8s-secrets / vault broker before opening the agent to
    /// real callers; see spec §6.3 and docs/framework-feedback.md step 4.
    /// </summary>
    public static IServiceCollection AddForagentCredentials(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = "Credentials")
    {
        services.AddOptions<InMemoryCredentialBrokerOptions>()
            .Bind(configuration.GetSection(sectionName));

        services.AddSingleton<ICredentialBroker, InMemoryCredentialBroker>();
        return services;
    }
}
