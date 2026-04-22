using Foragent.Browser;
using Foragent.Credentials;

namespace Foragent.Capabilities.SitePosting;

/// <summary>
/// Site-specific driver behind the generic <c>post-to-site</c> capability. One
/// implementation per site family (Bluesky, Mastodon, …). The capability
/// resolves an <see cref="ISitePoster"/> by matching <see cref="Site"/> to the
/// <c>site</c> input field, so site dispatch stays out of the capability.
/// </summary>
/// <remarks>
/// Not yet lifted to RockBot.A2A — it's Foragent-local until a second
/// framework consumer has the same shape. Noted in docs/framework-feedback.md.
/// </remarks>
public interface ISitePoster
{
    /// <summary>
    /// Case-insensitive site identifier (e.g. <c>bluesky</c>, <c>mastodon</c>).
    /// Matches the <c>site</c> input sent by the caller.
    /// </summary>
    string Site { get; }

    /// <summary>
    /// Authenticates (using <paramref name="credential"/>) and posts
    /// <paramref name="content"/>. Implementations must not log credential
    /// values or password form fields. Throws on failure; exception messages
    /// must not contain credential material.
    /// </summary>
    Task PostAsync(
        IBrowserSession session,
        CredentialReference credential,
        string content,
        CancellationToken cancellationToken);
}
