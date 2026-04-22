using System.Text;

namespace Foragent.Credentials;

/// <summary>
/// A resolved credential. <see cref="Values"/> carries the actual secret
/// material (passwords, tokens, cookies, cert material, storage state) keyed
/// by field name — the exact keys a capability consumes are
/// capability-specific (e.g. Bluesky's site poster reads <c>identifier</c> and
/// <c>password</c>).
/// </summary>
/// <remarks>
/// <para>
/// Values are <see cref="ReadOnlyMemory{T}"/> of <see cref="byte"/>, not
/// <see cref="string"/>: most real backends (k8s Secrets, certificate stores,
/// storage-state blobs) are byte-native, and forcing text conversion at the
/// broker boundary loses fidelity for binary material. Text-origin
/// credentials should use <see cref="FromText"/> / <see cref="RequireText"/>
/// to UTF-8 encode / decode at the edge.
/// </para>
/// <para>
/// Not a record: records generate a ToString that dumps every property,
/// which would round-trip secrets into logs the first time someone writes
/// <c>logger.LogDebug("{Cred}", cred)</c>. <see cref="ToString"/> is overridden
/// to expose only the id + kind.
/// </para>
/// </remarks>
public sealed class CredentialReference
{
    public CredentialReference(
        string id,
        string kind,
        IReadOnlyDictionary<string, ReadOnlyMemory<byte>> values)
    {
        Id = id;
        Kind = kind;
        Values = values;
    }

    /// <summary>
    /// Convenience factory for text-origin credentials (passwords, app
    /// passwords, API tokens, JSON storage-state blobs). UTF-8 encodes each
    /// value at the broker boundary so the internal representation stays
    /// byte-oriented without forcing callers to encode by hand.
    /// </summary>
    public static CredentialReference FromText(
        string id,
        string kind,
        IReadOnlyDictionary<string, string> values)
    {
        var encoded = new Dictionary<string, ReadOnlyMemory<byte>>(values.Count, StringComparer.Ordinal);
        foreach (var kvp in values)
            encoded[kvp.Key] = Encoding.UTF8.GetBytes(kvp.Value);
        return new CredentialReference(id, kind, encoded);
    }

    /// <summary>The opaque identifier the caller passed over A2A.</summary>
    public string Id { get; }

    /// <summary>
    /// Free-form label describing the shape of <see cref="Values"/>. Current
    /// usage: <c>username-password</c>. Future: <c>storage-state</c>, <c>totp</c>,
    /// <c>certificate</c>. Capabilities may validate this before attempting to
    /// use the credential.
    /// </summary>
    public string Kind { get; }

    /// <summary>
    /// Credential material. Never log this dictionary, never include it in
    /// A2A messages, never include it in exception messages.
    /// </summary>
    public IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Values { get; }

    /// <summary>
    /// Returns the raw bytes for <paramref name="key"/>. Throws if the key is
    /// absent or empty. Exception messages name the missing field but never
    /// echo any existing value.
    /// </summary>
    public ReadOnlyMemory<byte> Require(string key)
    {
        if (!Values.TryGetValue(key, out var value) || value.IsEmpty)
            throw new InvalidOperationException(
                $"Credential '{Id}' (kind '{Kind}') is missing required field '{key}'.");
        return value;
    }

    /// <summary>
    /// UTF-8 decode convenience for the common text-shaped field case
    /// (username, password, token, URL). For binary fields use
    /// <see cref="Require"/> and handle encoding directly.
    /// </summary>
    public string RequireText(string key) => Encoding.UTF8.GetString(Require(key).Span);

    public override string ToString() => $"CredentialReference(Id={Id}, Kind={Kind})";
}
