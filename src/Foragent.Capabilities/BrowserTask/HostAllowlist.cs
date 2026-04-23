namespace Foragent.Capabilities.BrowserTask;

/// <summary>
/// Parses and evaluates the per-task allowed-hosts list from spec §7.1.
/// Supports exact hosts (<c>bsky.app</c>), subdomain wildcards
/// (<c>*.example.com</c> matches <c>foo.example.com</c> but not
/// <c>example.com</c>), and the unrestricted sentinel (<c>*</c>). Empty
/// input is rejected at construction — there is no default-permissive mode.
/// </summary>
public sealed class HostAllowlist
{
    private readonly string[] _exact;
    private readonly string[] _suffix;
    private readonly bool _wildcardAll;

    private HostAllowlist(string[] exact, string[] suffix, bool wildcardAll)
    {
        _exact = exact;
        _suffix = suffix;
        _wildcardAll = wildcardAll;
    }

    /// <summary>The raw patterns, preserved for audit logging.</summary>
    public IReadOnlyList<string> Patterns { get; private init; } = [];

    /// <summary>
    /// Parses <paramref name="patterns"/>. Throws <see cref="ArgumentException"/>
    /// when the list is empty or contains an invalid pattern. Per-pattern
    /// rules:
    /// <list type="bullet">
    /// <item><description><c>*</c> alone — match all hosts.</description></item>
    /// <item><description><c>*.host</c> — match any subdomain of <c>host</c> (not <c>host</c> itself).</description></item>
    /// <item><description><c>host</c> — match the exact host (case-insensitive).</description></item>
    /// </list>
    /// </summary>
    public static HostAllowlist Parse(IEnumerable<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);

        var raw = new List<string>();
        var exact = new List<string>();
        var suffix = new List<string>();
        var wildcardAll = false;

        foreach (var entry in patterns)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;
            var pattern = entry.Trim().ToLowerInvariant();
            raw.Add(pattern);

            if (pattern == "*")
            {
                wildcardAll = true;
                continue;
            }

            if (pattern.StartsWith("*.", StringComparison.Ordinal))
            {
                var tail = pattern[2..];
                if (string.IsNullOrEmpty(tail) || tail.Contains('*') || tail.StartsWith('.'))
                    throw new ArgumentException(
                        $"Invalid allowlist pattern '{entry}'. Expected '*.domain.tld'.",
                        nameof(patterns));
                suffix.Add("." + tail);
                continue;
            }

            if (pattern.Contains('*'))
                throw new ArgumentException(
                    $"Invalid allowlist pattern '{entry}'. Only '*' or '*.host' wildcards are supported.",
                    nameof(patterns));

            exact.Add(pattern);
        }

        if (raw.Count == 0)
            throw new ArgumentException(
                "Allowlist is empty; an empty allowlist rejects all hosts (spec §7.1).",
                nameof(patterns));

        return new HostAllowlist([.. exact], [.. suffix], wildcardAll) { Patterns = raw };
    }

    /// <summary>Returns <c>true</c> if <paramref name="host"/> is permitted.</summary>
    public bool IsAllowed(string host)
    {
        if (string.IsNullOrEmpty(host))
            return false;
        if (_wildcardAll)
            return true;
        var normalized = host.ToLowerInvariant();
        foreach (var e in _exact)
            if (e == normalized) return true;
        foreach (var s in _suffix)
            if (normalized.EndsWith(s, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Returns <c>true</c> if <paramref name="url"/>'s host is permitted.</summary>
    public bool IsAllowed(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return IsAllowed(url.Host);
    }
}
