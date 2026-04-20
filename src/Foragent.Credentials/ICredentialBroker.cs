namespace Foragent.Credentials;

/// <summary>
/// Resolves credential references to their actual values inside the Foragent
/// process. Credential values never cross A2A boundaries or appear in logs.
/// </summary>
public interface ICredentialBroker
{
    /// <summary>
    /// Resolves a credential reference to a <see cref="CredentialReference"/>
    /// that can be used to supply credentials to a browser session.
    /// </summary>
    /// <param name="credentialId">The unique identifier of the credential.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A <see cref="CredentialReference"/> for the specified credential.</returns>
    Task<CredentialReference> ResolveAsync(
        string credentialId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A placeholder reference to a resolved credential.
/// </summary>
/// <param name="CredentialId">The unique identifier of the credential.</param>
public record CredentialReference(string CredentialId);
