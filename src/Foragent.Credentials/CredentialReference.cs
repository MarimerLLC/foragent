namespace Foragent.Credentials;

/// <summary>
/// A resolved credential. <see cref="Values"/> carries the actual secret
/// material (passwords, tokens, cookies) keyed by field name — the exact keys
/// a capability consumes are capability-specific (e.g. Bluesky's site poster
/// reads <c>identifier</c> and <c>password</c>).
/// </summary>
/// <remarks>
/// Not a record: records generate a ToString that dumps every property,
/// which would round-trip secrets into logs the first time someone writes
/// <c>logger.LogDebug("{Cred}", cred)</c>. <see cref="ToString"/> is overridden
/// to expose only the id + kind.
/// </remarks>
public sealed class CredentialReference
{
    public CredentialReference(string id, string kind, IReadOnlyDictionary<string, string> values)
    {
        Id = id;
        Kind = kind;
        Values = values;
    }

    /// <summary>The opaque identifier the caller passed over A2A.</summary>
    public string Id { get; }

    /// <summary>
    /// Free-form label describing the shape of <see cref="Values"/>. Current
    /// usage: <c>username-password</c>. Future: <c>storage-state</c>, <c>totp</c>.
    /// Capabilities may validate this before attempting to use the credential.
    /// </summary>
    public string Kind { get; }

    /// <summary>
    /// Credential material. Never log this dictionary, never include it in
    /// A2A messages, never include it in exception messages.
    /// </summary>
    public IReadOnlyDictionary<string, string> Values { get; }

    public string Require(string key)
    {
        if (!Values.TryGetValue(key, out var value) || string.IsNullOrEmpty(value))
            throw new InvalidOperationException(
                $"Credential '{Id}' (kind '{Kind}') is missing required field '{key}'.");
        return value;
    }

    public override string ToString() => $"CredentialReference(Id={Id}, Kind={Kind})";
}
