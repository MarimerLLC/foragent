using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using Xunit;

namespace Foragent.Agent.Tests;

/// <summary>
/// Covers the idempotency contract spec'd in <see cref="BskySeedSkillService"/>:
/// the seed is written once when absent, untouched when present.
/// </summary>
public class BskySeedSkillServiceTests
{
    [Fact]
    public async Task Seed_IsWritten_WhenSkillMissing()
    {
        var store = new FakeSkillStore();
        var service = new BskySeedSkillService(store, NullLogger<BskySeedSkillService>.Instance);

        await service.StartAsync(CancellationToken.None);

        Assert.True(store.Saved.ContainsKey("sites/bsky-app/login"));
        var skill = store.Saved["sites/bsky-app/login"];
        Assert.Contains("app password", skill.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sites/bsky-app/compose-post", skill.SeeAlso!);
    }

    [Fact]
    public async Task Seed_LeavesExistingSkillIntact()
    {
        var store = new FakeSkillStore();
        var existing = new Skill(
            Name: "sites/bsky-app/login",
            Summary: "operator-edited summary",
            Content: "operator-edited content",
            CreatedAt: DateTimeOffset.UtcNow.AddDays(-7),
            UpdatedAt: null,
            LastUsedAt: null,
            SeeAlso: []);
        await store.SaveAsync(existing);

        var service = new BskySeedSkillService(store, NullLogger<BskySeedSkillService>.Instance);
        await service.StartAsync(CancellationToken.None);

        var after = store.Saved["sites/bsky-app/login"];
        Assert.Equal("operator-edited summary", after.Summary);
        Assert.Equal("operator-edited content", after.Content);
    }

    [Fact]
    public async Task Seed_IsNoop_OnSecondStart()
    {
        var store = new FakeSkillStore();
        var service = new BskySeedSkillService(store, NullLogger<BskySeedSkillService>.Instance);

        await service.StartAsync(CancellationToken.None);
        var firstContent = store.Saved["sites/bsky-app/login"].Content;
        await service.StartAsync(CancellationToken.None);
        var secondContent = store.Saved["sites/bsky-app/login"].Content;

        Assert.Same(firstContent, secondContent);
    }
}
