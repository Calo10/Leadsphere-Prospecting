using System.Text.RegularExpressions;
using HtmlAgilityPack;
using LeadSphere.Discovery.Function.Infrastructure;
using LeadSphere.Discovery.Function.Models;

namespace LeadSphere.Discovery.Function.Services;

internal static class SocialLinkExtractor
{
    private static readonly Regex PersonalLinkedInHrefRegex = new(
        @"https?://(?:[\w.-]+\.)?linkedin\.com/in/[\w%-]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly (string Key, Regex Pattern)[] PlatformPatterns =
    [
        ("linkedin", new Regex(@"https?://(?:[\w.-]+\.)?linkedin\.com/company/[\w%-./]+", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("twitter", new Regex(@"https?://(?:[\w.-]+\.)?(?:twitter\.com|x\.com)/[\w%-./]+", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("facebook", new Regex(@"https?://(?:[\w.-]+\.)?facebook\.com/[\w%-./]+", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("instagram", new Regex(@"https?://(?:[\w.-]+\.)?instagram\.com/[\w%-./]+", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("crunchbase", new Regex(@"https?://(?:[\w.-]+\.)?crunchbase\.com/organization/[\w%-./]+", RegexOptions.IgnoreCase | RegexOptions.Compiled))
    ];

    public static Dictionary<string, string> ExtractFromHtml(string? html)
    {
        var links = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(html))
            return links;

        foreach (var (key, pattern) in PlatformPatterns)
        {
            var match = pattern.Match(html);
            if (match.Success)
                links[key] = NormalizeUrl(match.Value);
        }

        var document = new HtmlDocument();
        document.LoadHtml(html);

        foreach (var anchor in document.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>())
        {
            var href = anchor.GetAttributeValue("href", string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(href))
                continue;

            href = HtmlEntity.DeEntitize(href);
            if (href.StartsWith("//", StringComparison.Ordinal))
                href = "https:" + href;

            foreach (var (key, pattern) in PlatformPatterns)
            {
                if (links.ContainsKey(key))
                    continue;

                if (pattern.IsMatch(href))
                    links[key] = NormalizeUrl(href);
            }
        }

        return links;
    }

    public static List<AiContactData> ExtractPersonalLinkedInContacts(string? html)
    {
        var contacts = new List<AiContactData>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(html))
            return contacts;

        var document = new HtmlDocument();
        document.LoadHtml(html);

        foreach (var anchor in document.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>())
        {
            var href = HtmlEntity.DeEntitize(anchor.GetAttributeValue("href", string.Empty).Trim());
            if (href.StartsWith("//", StringComparison.Ordinal))
                href = "https:" + href;

            var match = PersonalLinkedInHrefRegex.Match(href);
            if (!match.Success)
                continue;

            var url = LinkedInContactUrl.NormalizePersonal(match.Value);
            if (url is null || !seen.Add(url))
                continue;

            var name = NormalizePersonName(HtmlEntity.DeEntitize(anchor.InnerText));
            if (name is null)
                continue;

            var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            contacts.Add(new AiContactData
            {
                FullName = name,
                FirstName = parts[0],
                LastName = parts.Length > 1 ? string.Join(' ', parts.Skip(1)) : null,
                LinkedInUrl = url
            });
        }

        return contacts;
    }

    private static string? NormalizePersonName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var name = Regex.Replace(raw, @"\s+", " ").Trim();
        if (name.Contains("LinkedIn", StringComparison.OrdinalIgnoreCase))
            return null;

        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 2 or > 4)
            return null;

        if (parts.Any(p => p.Length < 2 || !char.IsLetter(p[0])))
            return null;

        return name;
    }

    public static List<string> ExtractLogoCandidates(string? html, Uri? baseUri)
    {
        var candidates = new List<string>();
        if (string.IsNullOrWhiteSpace(html))
            return candidates;

        var document = new HtmlDocument();
        document.LoadHtml(html);

        AddMetaContent(candidates, document, "property", "og:image");
        AddMetaContent(candidates, document, "name", "twitter:image");

        foreach (var link in document.DocumentNode.SelectNodes("//link[@rel]") ?? Enumerable.Empty<HtmlNode>())
        {
            var rel = link.GetAttributeValue("rel", string.Empty);
            if (!rel.Contains("icon", StringComparison.OrdinalIgnoreCase)
                && !rel.Contains("apple-touch-icon", StringComparison.OrdinalIgnoreCase))
                continue;

            var href = link.GetAttributeValue("href", string.Empty).Trim();
            var absolute = ToAbsoluteUrl(href, baseUri);
            if (!string.IsNullOrWhiteSpace(absolute))
                candidates.Add(absolute);
        }

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AddMetaContent(List<string> candidates, HtmlDocument document, string attribute, string value)
    {
        var node = document.DocumentNode.SelectSingleNode($"//meta[@{attribute}='{value}']");
        var content = node?.GetAttributeValue("content", string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(content))
            candidates.Add(content);
    }

    private static string? ToAbsoluteUrl(string href, Uri? baseUri)
    {
        if (string.IsNullOrWhiteSpace(href))
            return null;

        href = HtmlEntity.DeEntitize(href);
        if (href.StartsWith("//", StringComparison.Ordinal))
            href = "https:" + href;

        if (Uri.TryCreate(href, UriKind.Absolute, out var absolute))
            return absolute.ToString();

        if (baseUri is null)
            return null;

        return Uri.TryCreate(baseUri, href, out var resolved) ? resolved.ToString() : null;
    }

    private static string NormalizeUrl(string url)
    {
        url = url.Trim().TrimEnd('/');
        var q = url.IndexOf('?', StringComparison.Ordinal);
        return q > 0 ? url[..q] : url;
    }
}
