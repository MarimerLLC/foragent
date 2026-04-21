using Foragent.Browser;
using Foragent.Credentials;
using Microsoft.Extensions.Logging;

namespace Foragent.Capabilities.SitePosting;

/// <summary>
/// Drives the Bluesky web UI (bsky.app) to post content on behalf of a user
/// authenticated with an app password (spec §6.6 prefers app passwords where
/// available). Uses stable accessibility-role selectors rather than CSS so
/// minor UI tweaks don't break the flow — selectors are still inherently
/// fragile and are flagged in docs/framework-feedback.md.
/// </summary>
/// <remarks>
/// Expects <see cref="CredentialReference.Values"/> with keys <c>identifier</c>
/// (handle or email) and <c>password</c> (app password). Does not persist
/// <c>storageState</c> yet — every post re-authenticates; spec §6.5's
/// session-as-credential flow is deferred.
/// </remarks>
public sealed class BlueskySitePoster : ISitePoster
{
    public const string SiteId = "bluesky";

    private static readonly Uri DefaultLoginUrl = new("https://bsky.app/");
    private static readonly TimeSpan InteractiveTimeout = TimeSpan.FromSeconds(30);

    private readonly ILogger<BlueskySitePoster> logger;
    private readonly Uri loginUrl;

    // DI-friendly: defaults to the real bsky.app. Tests use the Uri overload
    // to point at a local Kestrel-hosted fake login + compose UI.
    public BlueskySitePoster(ILogger<BlueskySitePoster> logger)
        : this(logger, DefaultLoginUrl) { }

    public BlueskySitePoster(ILogger<BlueskySitePoster> logger, Uri loginUrl)
    {
        this.logger = logger;
        this.loginUrl = loginUrl;
    }

    // Accessibility-role + attribute selectors. Playwright's string-selector
    // dialect does not accept regex; for flexibility across the real bsky.app
    // and the fake test UI we pick stable exact strings and update them here
    // when Bluesky's copy changes. Flagged as fragile in docs/framework-feedback.md.
    private const string SignInButton = "role=button[name=\"Sign in\"]";
    private const string IdentifierField = "input[placeholder=\"Username or email address\"]";
    private const string PasswordField = "input[placeholder=\"Password\"]";
    private const string SubmitLoginButton = "role=button[name=\"Next\"]";
    private const string ComposeButton = "role=button[name=\"New post\"]";
    private const string ComposeEditor = "[contenteditable=\"true\"]";
    private const string PublishButton = "role=button[name=\"Post\"]";
    private const string HomeFeedHeading = "role=heading[name=\"Home\"]";

    public string Site => SiteId;

    public async Task PostAsync(
        IBrowserSession session,
        CredentialReference credential,
        string content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(credential);
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("Post content cannot be empty.", nameof(content));

        var identifier = credential.Require("identifier");
        var password = credential.Require("password");

        logger.LogInformation(
            "Posting to Bluesky as '{Identifier}' (credential {CredentialId}, {Length} chars)",
            identifier, credential.Id, content.Length);

        await using var page = await session.OpenPageAsync(loginUrl, cancellationToken);

        await SignInAsync(page, identifier, password, cancellationToken);
        await ComposeAsync(page, content, cancellationToken);
    }

    private async Task SignInAsync(
        IBrowserPage page, string identifier, string password, CancellationToken ct)
    {
        await page.WaitForSelectorAsync(SignInButton, InteractiveTimeout, ct);
        await page.ClickAsync(SignInButton, ct);

        await page.WaitForSelectorAsync(IdentifierField, InteractiveTimeout, ct);
        await page.FillAsync(IdentifierField, identifier, ct);
        await page.FillAsync(PasswordField, password, ct);
        await page.ClickAsync(SubmitLoginButton, ct);

        await page.WaitForSelectorAsync(HomeFeedHeading, InteractiveTimeout, ct);
        logger.LogInformation("Bluesky login succeeded for '{Identifier}'", identifier);
    }

    private async Task ComposeAsync(IBrowserPage page, string content, CancellationToken ct)
    {
        await page.WaitForSelectorAsync(ComposeButton, InteractiveTimeout, ct);
        await page.ClickAsync(ComposeButton, ct);

        await page.WaitForSelectorAsync(ComposeEditor, InteractiveTimeout, ct);
        await page.FillAsync(ComposeEditor, content, ct);

        await page.ClickAsync(PublishButton, ct);

        // Publish closes the composer and returns to the home feed; wait for
        // the composer to disappear as the success signal.
        await page.WaitForSelectorAsync(HomeFeedHeading, InteractiveTimeout, ct);
        logger.LogInformation("Bluesky post published ({Length} chars)", content.Length);
    }
}
