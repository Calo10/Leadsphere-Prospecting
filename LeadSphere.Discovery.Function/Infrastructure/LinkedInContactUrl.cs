using System.Text.RegularExpressions;

namespace LeadSphere.Discovery.Function.Infrastructure;

internal static class LinkedInContactUrl
{
    private static readonly Regex PersonalProfileRegex = new(
        @"https?://(?:[\w.-]+\.)?linkedin\.com/in/([\w%-]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CompanyPathRegex = new(
        @"linkedin\.com/(?:[a-z]{2}/)?(?:mwlite/)?(?:company|school|showcase)(?:/|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CompanySlugRegex = new(
        @"linkedin\.com/(?:[a-z]{2}/)?(?:mwlite/)?company/([\w%-]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> ReservedPersonalSlugs = new(StringComparer.OrdinalIgnoreCase)
    {
        "company", "school", "showcase", "jobs", "sales", "learning", "feed",
        "login", "signup", "in", "pub", "pulse", "groups", "admin", "me"
    };

    public static string? NormalizePersonal(string? url, string? companyLinkedInUrl = null)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        url = url.Trim().Trim('"', '\'', '<', '>', '[', ']');
        if (IsCompanyProfile(url))
            return null;

        if (!url.Contains("://", StringComparison.Ordinal))
            url = "https://" + url.TrimStart('/');

        if (IsCompanyProfile(url))
            return null;

        var match = PersonalProfileRegex.Match(url);
        if (!match.Success)
            return null;

        var slug = match.Groups[1].Value;
        if (string.IsNullOrWhiteSpace(slug) || ReservedPersonalSlugs.Contains(slug))
            return null;

        var companySlug = ExtractCompanySlug(companyLinkedInUrl);
        if (!string.IsNullOrWhiteSpace(companySlug)
            && string.Equals(slug, companySlug, StringComparison.OrdinalIgnoreCase))
            return null;

        var normalized = match.Value.Trim().TrimEnd('/');
        var q = normalized.IndexOf('?', StringComparison.Ordinal);
        return q > 0 ? normalized[..q] : normalized;
    }

    public static bool IsPersonalProfile(string? url, string? companyLinkedInUrl = null) =>
        NormalizePersonal(url, companyLinkedInUrl) is not null;

    public static bool IsCompanyProfile(string? url) =>
        !string.IsNullOrWhiteSpace(url) && CompanyPathRegex.IsMatch(url);

    public static string? ExtractCompanySlug(string? companyLinkedInUrl)
    {
        if (string.IsNullOrWhiteSpace(companyLinkedInUrl))
            return null;

        var match = CompanySlugRegex.Match(companyLinkedInUrl.Trim());
        return match.Success ? match.Groups[1].Value : null;
    }
}
