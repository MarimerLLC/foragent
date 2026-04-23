namespace Foragent.Capabilities;

/// <summary>
/// Shared helpers for constructing skill names that pass RockBot 0.9's
/// <c>FileSkillStore.ValidateName</c> — only alphanumeric, hyphens,
/// underscores, and <c>/</c> are allowed. Real hosts (<c>bsky.app</c>,
/// <c>apple.com</c>) contain dots, so anything that embeds a host inside a
/// skill name must go through <see cref="SanitizeHost"/>.
/// </summary>
internal static class SkillNaming
{
    /// <summary>
    /// Replaces characters the skill-store validator rejects. Dots become
    /// hyphens (<c>bsky.app</c> → <c>bsky-app</c>) — readable, reversible
    /// by humans, and keeps the host recognizable in the stored skill path.
    /// </summary>
    public static string SanitizeHost(string host) =>
        host.Replace('.', '-');
}
