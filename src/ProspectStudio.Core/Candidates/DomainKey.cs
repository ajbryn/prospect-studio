namespace ProspectStudio.Core.Candidates;

/// <summary>
/// The first dedupe key of technical-design §7.2: the registrable domain of a website, with the
/// generic hosts ignored. Two unrelated companies that both list a Facebook page share that host, so
/// treating it as an identity would merge them.
/// </summary>
public static class DomainKey
{
    /// <summary>The hosts §7.2 lists as generic, so they never form a key.</summary>
    public static IReadOnlySet<string> GenericHosts { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "facebook.com", "instagram.com", "linkedin.com", "yelp.com", "google.com",
        "business.site", "wixsite.com", "godaddysites.com", "squarespace.com", "sites.google.com",
    };

    /// <summary>
    /// Multi-label public suffixes the POC knows about (§7.2). A full public-suffix list is out of
    /// scope; every fixture domain is single-label, so this path is deliberately narrow.
    /// </summary>
    private static readonly HashSet<string> _multiLabelSuffixes = new(StringComparer.Ordinal)
    {
        "co.uk", "com.au", "co.nz", "com.br",
    };

    /// <summary>
    /// The lower-case registrable domain of <paramref name="website"/>, or <c>null</c> when there is no
    /// website, it cannot be parsed, or its host is generic. Real Overture values include bare hosts
    /// (<c>chartersupply.com</c>) and plain <c>http://</c> URLs, so both forms must work.
    /// </summary>
    public static string? For(string? website)
    {
        var host = Host(website);
        if (host is null)
        {
            return null;
        }

        var labels = host.Split('.');
        if (labels.Length >= 2 && string.Equals(labels[0], "www", StringComparison.Ordinal))
        {
            labels = labels[1..];
        }

        if (labels.Length < 2)
        {
            return null;
        }

        var registrable = string.Join('.', labels[^2..]);
        if (labels.Length >= 3 && _multiLabelSuffixes.Contains(registrable))
        {
            registrable = string.Join('.', labels[^3..]);
        }

        return GenericHosts.Contains(registrable) || GenericHosts.Contains(string.Join('.', labels))
            ? null
            : registrable;
    }

    private static string? Host(string? website)
    {
        if (string.IsNullOrWhiteSpace(website))
        {
            return null;
        }

        var value = website.Trim();
        var scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            value = value[(scheme + 3)..];
        }
        else if (value.Contains(':', StringComparison.Ordinal) && !value.Contains('/', StringComparison.Ordinal))
        {
            // 'mailto:sales@…' is not a website, and neither is any other opaque scheme.
            return null;
        }

        var authority = value.Split('/', '?', '#')[0];
        var port = authority.LastIndexOf(':');
        if (port >= 0)
        {
            authority = authority[..port];
        }

        var at = authority.LastIndexOf('@');
        if (at >= 0)
        {
            authority = authority[(at + 1)..];
        }

        authority = authority.ToLowerInvariant();

        return IsHostname(authority) ? authority : null;
    }

    private static bool IsHostname(string value)
    {
        if (value.Length == 0 || !value.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var label in value.Split('.'))
        {
            if (label.Length == 0 || !label.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
            {
                return false;
            }
        }

        return true;
    }
}
