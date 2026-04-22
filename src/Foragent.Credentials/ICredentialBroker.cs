namespace Foragent.Credentials;

/// <summary>
/// Resolves credential identifiers to credential values inside the Foragent
/// process. The calling agent passes only the identifier across the A2A
/// boundary (spec §6.2); <see cref="ResolveAsync"/> produces the material that
/// actually unlocks a browser session.
/// </summary>
/// <remarks>
/// Implementations MUST NOT log credential contents or surface them in
/// exception messages. Callers (Foragent capabilities) MUST NOT log
/// <see cref="CredentialReference.Values"/> or include them in A2A responses.
/// Broker queries are expected to be scoped to a single tenant — the tenant
/// id flows from A2A caller identity, not from request payloads (spec §7.5).
/// Tenancy is not yet enforced at the broker interface; see
/// docs/framework-feedback.md step 4.
/// </remarks>
public interface ICredentialBroker
{
    /// <summary>
    /// Resolves a credential identifier. Throws
    /// <see cref="CredentialNotFoundException"/> if no credential with that
    /// id is configured.
    /// </summary>
    Task<CredentialReference> ResolveAsync(
        string credentialId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Thrown when <see cref="ICredentialBroker.ResolveAsync"/> cannot find a
/// credential with the requested id. Carries the id but never any credential
/// material.
/// </summary>
public sealed class CredentialNotFoundException(string credentialId)
    : Exception($"No credential configured with id '{credentialId}'.")
{
    public string CredentialId { get; } = credentialId;
}
