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
        Assert.Equal("rocky.bsky.social", cred.RequireText("identifier"));
        Assert.Equal("app-pass-xyz", cred.RequireText("password"));
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
        var cred = CredentialReference.FromText(
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
        var cred = CredentialReference.FromText(
            "id", "username-password",
            new Dictionary<string, string> { ["identifier"] = "u" });

        var ex = Assert.Throws<InvalidOperationException>(() => cred.Require("password"));
        Assert.Contains("password", ex.Message);
        // The exception is about a missing field, so it can safely name the key
        // — but it must never echo any existing value.
        Assert.DoesNotContain("u", ex.Message.Split('\'')[^1]);
    }

    [Fact]
    public void FromText_RoundTripsThroughUtf8()
    {
        var cred = CredentialReference.FromText(
            "id", "username-password",
            new Dictionary<string, string>
            {
                ["identifier"] = "röcky@例え.test",
                ["password"] = "\u00a0secret\u2603"
            });

        // UTF-8 round trip through RequireText should reproduce the original
        // strings exactly — confirms we don't lose non-ASCII content at the
        // encoding boundary.
        Assert.Equal("röcky@例え.test", cred.RequireText("identifier"));
        Assert.Equal("\u00a0secret\u2603", cred.RequireText("password"));
    }

    [Fact]
    public void Values_AreReadOnlyMemoryBytes()
    {
        // Sanity check: binary-origin credentials (cert material, storage
        // state blobs) go through the direct ctor without double-encoding.
        var bytes = new byte[] { 0x30, 0x82, 0x01, 0x00 };
        var cred = new CredentialReference(
            "id", "certificate",
            new Dictionary<string, ReadOnlyMemory<byte>> { ["der"] = bytes });

        Assert.True(cred.Require("der").Span.SequenceEqual(bytes));
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
