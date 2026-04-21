using Foragent.Capabilities;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.A2A;
using Xunit;

namespace Foragent.Agent.Tests;

public class FetchPageTitleCapabilityTests
{
    [Fact]
    public async Task ReturnsTitle_FromBrowser()
    {
        var factory = new StubBrowserSessionFactory
        {
            TitleResponder = (_, _) => Task.FromResult<string?>("Hello World")
        };
        var capability = new FetchPageTitleCapability(factory, NullLogger<FetchPageTitleCapability>.Instance);
        var (context, _) = TestContext.Build();

        var result = await capability.ExecuteAsync(
            TestContext.Request("fetch-page-title", "https://example.com"),
            context);

        Assert.Equal(AgentTaskState.Completed, result.State);
        Assert.Equal("Hello World", TestContext.TextOf(result));
    }

    [Fact]
    public async Task ReportsNoTitle_WhenBrowserReturnsNull()
    {
        var factory = new StubBrowserSessionFactory
        {
            TitleResponder = (_, _) => Task.FromResult<string?>(null)
        };
        var capability = new FetchPageTitleCapability(factory, NullLogger<FetchPageTitleCapability>.Instance);
        var (context, _) = TestContext.Build();

        var result = await capability.ExecuteAsync(
            TestContext.Request("fetch-page-title", "https://example.com"),
            context);

        Assert.Equal("(no title)", TestContext.TextOf(result));
    }

    [Fact]
    public async Task ReportsError_WhenBrowserThrows()
    {
        var factory = new StubBrowserSessionFactory
        {
            TitleResponder = (_, _) => Task.FromException<string?>(new InvalidOperationException("boom"))
        };
        var capability = new FetchPageTitleCapability(factory, NullLogger<FetchPageTitleCapability>.Instance);
        var (context, _) = TestContext.Build();

        var result = await capability.ExecuteAsync(
            TestContext.Request("fetch-page-title", "https://example.com"),
            context);

        Assert.Contains("Fetch failed: boom", TestContext.TextOf(result));
    }

    [Fact]
    public async Task RejectsNonAbsoluteUrl_WithoutCreatingSession()
    {
        var factory = new StubBrowserSessionFactory();
        var capability = new FetchPageTitleCapability(factory, NullLogger<FetchPageTitleCapability>.Instance);
        var (context, _) = TestContext.Build();

        var result = await capability.ExecuteAsync(
            TestContext.Request("fetch-page-title", "not a url"),
            context);

        Assert.Equal(0, factory.SessionsCreated);
        Assert.Contains("absolute http(s) URL", TestContext.TextOf(result));
    }

    [Fact]
    public async Task DisposesSession_AfterUse()
    {
        var factory = new StubBrowserSessionFactory
        {
            TitleResponder = (_, _) => Task.FromResult<string?>("t")
        };
        var capability = new FetchPageTitleCapability(factory, NullLogger<FetchPageTitleCapability>.Instance);
        var (context, _) = TestContext.Build();

        await capability.ExecuteAsync(
            TestContext.Request("fetch-page-title", "https://example.com"),
            context);

        Assert.Equal(1, factory.SessionsCreated);
        Assert.Equal(1, factory.SessionsDisposed);
    }

    [Fact]
    public async Task AcceptsUrlFromJsonShim()
    {
        var factory = new StubBrowserSessionFactory
        {
            TitleResponder = (_, _) => Task.FromResult<string?>("Hello")
        };
        var capability = new FetchPageTitleCapability(factory, NullLogger<FetchPageTitleCapability>.Instance);
        var (context, _) = TestContext.Build();

        var result = await capability.ExecuteAsync(
            TestContext.Request("fetch-page-title", """{"url":"https://example.com"}"""),
            context);

        Assert.Equal("Hello", TestContext.TextOf(result));
    }
}
