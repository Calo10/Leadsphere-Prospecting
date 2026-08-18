using System.Text.RegularExpressions;

namespace LeadSphere.Discovery.Function.Infrastructure;

internal static class LinkedInContactUrl
{
    private static readonly Regex PersonalProfileRegex = new(
        @"https?://(?:[\w.-]+\.)?linkedin\.com/in/[\w%-]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string? NormalizePersonal(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (IsCompanyProfile(url))
            return null;

        var match = PersonalProfileRegex.Match(url.Trim());
        if (!match.Success)
            return null;

        url = match.Value.Trim().TrimEnd('/');
        var q = url.IndexOf('?', StringComparison.Ordinal);
        return q > 0 ? url[..q] : url;
    }

    public static bool IsPersonalProfile(string? url) =>
        NormalizePersonal(url) is not null;

    public static bool IsCompanyProfile(string? url) =>
        !string.IsNullOrWhiteSpace(url) &&
        url.Contains("linkedin.com/company/", StringComparison.OrdinalIgnoreCase);
}
