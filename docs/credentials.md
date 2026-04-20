# Credential Broker Pattern

Foragent never exposes credential values over A2A or in agent prompts.
Instead, callers pass a **credential reference** (an opaque string ID).

## How it works

1. The A2A caller includes a `credentialId` in the capability request
2. `ICredentialBroker.ResolveAsync` is called inside the Foragent process
3. The broker retrieves the actual credential value from a secret store
4. The browser session uses the value directly; it is never serialized or logged

## ICredentialBroker

```csharp
public interface ICredentialBroker
{
    Task<CredentialReference> ResolveAsync(
        string credentialId,
        CancellationToken cancellationToken = default);
}
```

Implementations are pluggable. Bring your own secret store (Azure Key Vault,
AWS Secrets Manager, HashiCorp Vault, environment variables for local dev,
etc.).

## TODO

- [ ] Define `CredentialReference` fields needed by browser session
- [ ] Implement an environment-variable-backed broker for local development
- [ ] Document how to register a custom broker
