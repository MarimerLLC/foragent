using Foragent.Credentials;
using Microsoft.Extensions.Options;
using Xunit;

namespace Foragent.Agent.Tests;

public class InMemoryCredentialBrokerTests
{
    [Fact]
    public async Task ResolvesConfiguredCredential()
    {
        var options = Options.Create(new InMemoryCredentialBrokerOptions
        {
            Credentials = new()
            {
                ["rockbot/social/bluesky-rocky"] = new InMemoryCredentialEntry
                {
                    Kind = "username-password",
                    Values = new()
                    {
                        ["identifier"] = "rocky.bsky.social",
                        ["password"] = "app-pass-xyz"
                    }
                }
            }
        });
        var broker = new InMemoryCredentialBroker(Monitor(options));

        var cred = await broker.ResolveAsync("rockbot/social/bluesky-rocky");

        Assert.Equal("rockbot/social/bluesky-rocky", cred.Id);
        Assert.Equal("username-password", cred.Kind);
        Assert.Equal("rocky.bsky.social", cred.Require("identifier"));
        Assert.Equal("app-pass-xyz", cred.Require("password"));
    }

    [Fact]
    public async Task Throws_WhenCredentialMissing()
    {
        var broker = new InMemoryCredentialBroker(Monitor(Options.Create(new InMemoryCredentialBrokerOptions())));

        var ex = await Assert.ThrowsAsync<CredentialNotFoundException>(
            () => broker.ResolveAsync("missing/id"));

        Assert.Equal("missing/id", ex.CredentialId);
        Assert.DoesNotContain("password", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToString_DoesNotExposeValues()
    {
        var cred = new CredentialReference(
            "rockbot/social/bluesky-rocky",
            "username-password",
            new Dictionary<string, string> { ["password"] = "super-secret-123" });

        var rendered = cred.ToString();

        Assert.Contains("rockbot/social/bluesky-rocky", rendered);
        Assert.DoesNotContain("super-secret-123", rendered);
    }

    [Fact]
    public void Require_Throws_WhenFieldMissing()
    {
        var cred = new CredentialReference(
            "id", "username-password",
            new Dictionary<string, string> { ["identifier"] = "u" });

        var ex = Assert.Throws<InvalidOperationException>(() => cred.Require("password"));
        Assert.Contains("password", ex.Message);
        // The exception is about a missing field, so it can safely name the key
        // — but it must never echo any existing value.
        Assert.DoesNotContain("u", ex.Message.Split('\'')[^1]);
    }

    private static IOptionsMonitor<T> Monitor<T>(IOptions<T> options) where T : class =>
        new StaticOptionsMonitor<T>(options.Value);

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
