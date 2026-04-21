using Foragent.Browser;
using Foragent.Capabilities;
using Foragent.Capabilities.SitePosting;
using Foragent.Credentials;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.A2A;
using Xunit;

namespace Foragent.Agent.Tests;

public class PostToSiteCapabilityTests
{
    [Fact]
    public async Task DispatchesToSitePoster_OnSuccess()
    {
        var poster = new CapturingPoster("bluesky");
        var capability = Build(poster, broker: SingleCredential("rockbot/social/bluesky-rocky"));
        var (context, _) = TestContext.Build();

        var result = await capability.ExecuteAsync(
            TestContext.Request("post-to-site",
                """{"site":"bluesky","credentialId":"rockbot/social/bluesky-rocky","content":"hello world"}"""),
            context);

        Assert.Equal(AgentTaskState.Completed, result.State);
        Assert.Equal("Posted to bluesky.", TestContext.TextOf(result));
        Assert.Equal("hello world", poster.LastContent);
        Assert.Equal("rockbot/social/bluesky-rocky", poster.LastCredentialId);
    }

    [Fact]
    public async Task AcceptsInput_FromMetadata()
    {
        var poster = new CapturingPoster("bluesky");
        var capability = Build(poster, broker: SingleCredential("cred-id"));
        var (context, _) = TestContext.Build();
        var request = TestContext.RequestWithMetadata(
            "post-to-site",
            messageMetadata: new Dictionary<string, string>
            {
                ["site"] = "bluesky",
                ["credentialId"] = "cred-id",
                ["content"] = "via metadata"
            });

        var result = await capability.ExecuteAsync(request, context);

        Assert.Equal(AgentTaskState.Completed, result.State);
        Assert.Equal("via metadata", poster.LastContent);
    }

    [Fact]
    public async Task ReportsMissingCredential_WithoutCreatingSession()
    {
        var poster = new CapturingPoster("bluesky");
        var factory = new StubBrowserSessionFactory();
        var capability = new PostToSiteCapability(
            factory,
            new StubCredentialBroker(),
            [poster],
            NullLogger<PostToSiteCapability>.Instance);
        var (context, _) = TestContext.Build();

        var result = await capability.ExecuteAsync(
            TestContext.Request("post-to-site",
                """{"site":"bluesky","credentialId":"ghost","content":"hi"}"""),
            context);

        Assert.Equal(0, factory.SessionsCreated);
        Assert.Contains("'ghost'", TestContext.TextOf(result));
        Assert.Contains("not configured", TestContext.TextOf(result));
    }

    [Fact]
    public async Task ReportsUnknownSite()
    {
        var poster = new CapturingPoster("bluesky");
        var capability = Build(poster, broker: SingleCredential("cred-id"));
        var (context, _) = TestContext.Build();

        var result = await capability.ExecuteAsync(
            TestContext.Request("post-to-site",
                """{"site":"mastodon","credentialId":"cred-id","content":"hi"}"""),
            context);

        Assert.Contains("mastodon", TestContext.TextOf(result));
        Assert.Contains("Known sites", TestContext.TextOf(result));
    }

    [Fact]
    public async Task ReportsInvalidJson()
    {
        var poster = new CapturingPoster("bluesky");
        var capability = Build(poster, broker: SingleCredential("cred-id"));
        var (context, _) = TestContext.Build();

        var result = await capability.ExecuteAsync(
            TestContext.Request("post-to-site", "{not json"),
            context);

        Assert.Contains("JSON", TestContext.TextOf(result));
    }

    [Fact]
    public async Task ReportsMissingFields()
    {
        var poster = new CapturingPoster("bluesky");
        var capability = Build(poster, broker: SingleCredential("cred-id"));
        var (context, _) = TestContext.Build();

        var result = await capability.ExecuteAsync(
            TestContext.Request("post-to-site", """{"site":"bluesky"}"""),
            context);

        Assert.Contains("credentialId", TestContext.TextOf(result));
    }

    [Fact]
    public async Task ScrubsExceptionMessage_OnPosterFailure()
    {
        // If a poster throws with credential-shaped text in the message, the
        // capability must NOT echo it back — the caller sees a generic
        // failure message; the full exception is only logged.
        var poster = new ThrowingPoster("bluesky", "secret-pw-leak");
        var capability = Build(poster, broker: SingleCredential("cred-id"));
        var (context, _) = TestContext.Build();

        var result = await capability.ExecuteAsync(
            TestContext.Request("post-to-site",
                """{"site":"bluesky","credentialId":"cred-id","content":"hi"}"""),
            context);

        var text = TestContext.TextOf(result);
        Assert.Equal("Post to bluesky failed.", text);
        Assert.DoesNotContain("secret-pw-leak", text);
    }

    private static PostToSiteCapability Build(
        ISitePoster poster,
        ICredentialBroker broker)
    {
        var factory = new StubBrowserSessionFactory();
        return new PostToSiteCapability(
            factory,
            broker,
            [poster],
            NullLogger<PostToSiteCapability>.Instance);
    }

    private static StubCredentialBroker SingleCredential(string id) =>
        new()
        {
            Credentials =
            {
                [id] = new CredentialReference(id, "username-password",
                    new Dictionary<string, string>
                    {
                        ["identifier"] = "u",
                        ["password"] = "p"
                    })
            }
        };

    private sealed class CapturingPoster(string site) : ISitePoster
    {
        public string Site { get; } = site;
        public string? LastContent { get; private set; }
        public string? LastCredentialId { get; private set; }

        public Task PostAsync(
            IBrowserSession session,
            CredentialReference credential,
            string content,
            CancellationToken ct)
        {
            LastContent = content;
            LastCredentialId = credential.Id;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingPoster(string site, string sensitiveText) : ISitePoster
    {
        public string Site { get; } = site;

        public Task PostAsync(
            IBrowserSession session,
            CredentialReference credential,
            string content,
            CancellationToken ct) =>
            throw new InvalidOperationException($"Auth failed — {sensitiveText}");
    }
}

internal sealed class StubCredentialBroker : ICredentialBroker
{
    public Dictionary<string, CredentialReference> Credentials { get; } = new();

    public Task<CredentialReference> ResolveAsync(string credentialId, CancellationToken ct = default)
    {
        if (!Credentials.TryGetValue(credentialId, out var cred))
            throw new CredentialNotFoundException(credentialId);
        return Task.FromResult(cred);
    }
}
