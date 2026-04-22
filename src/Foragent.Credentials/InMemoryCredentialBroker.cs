using Microsoft.Extensions.Options;

namespace Foragent.Credentials;

/// <summary>
/// Reads credentials from <see cref="InMemoryCredentialBrokerOptions"/> —
/// typically bound from a <c>Credentials</c> config section, which in dev is
/// populated via user-secrets (never appsettings.json). Spec §6.3 marks this
/// as dev/test only; production deployments plug in a k8s-secrets or vault
/// broker instead.
/// </summary>
public sealed class InMemoryCredentialBroker(
    IOptionsMonitor<InMemoryCredentialBrokerOptions> options) : ICredentialBroker
{
    public Task<CredentialReference> ResolveAsync(
        string credentialId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = options.CurrentValue;
        if (!snapshot.Credentials.TryGetValue(credentialId, out var entry))
            throw new CredentialNotFoundException(credentialId);

        // Config binding is text-native (appsettings / user-secrets / env vars
        // all hand us strings). CredentialReference is byte-shaped for backend
        // generality — k8s Secrets and cert stores are byte-native — so UTF-8
        // encode at the boundary here rather than pushing encoding into every
        // consumer.
        return Task.FromResult(CredentialReference.FromText(
            credentialId,
            entry.Kind,
            entry.Values));
    }
}

public sealed class InMemoryCredentialBrokerOptions
{
    /// <summary>Keyed by the credential id a caller passes to the broker.</summary>
    public Dictionary<string, InMemoryCredentialEntry> Credentials { get; init; } = new();
}

public sealed class InMemoryCredentialEntry
{
    public string Kind { get; init; } = "username-password";
    public Dictionary<string, string> Values { get; init; } = new();
}
