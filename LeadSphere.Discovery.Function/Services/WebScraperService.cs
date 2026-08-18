using System.Net;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using LeadSphere.Discovery.Function.Infrastructure;
using LeadSphere.Discovery.Function.Models;
using Microsoft.Extensions.Logging;

namespace LeadSphere.Discovery.Function.Services;

public interface IWebScraperService
{
    Task<CompanyCandidate> ScrapeCompanyAsync(WebSearchResult searchResult, string? locationHint, CancellationToken cancellationToken);
}

public sealed class WebScraperService : IWebScraperService
{
    private static readonly string[] PagePaths =
    [
        "", "/about", "/about-us", "/contact", "/contact-us",
        "/team", "/our-team", "/leadership", "/management", "/executive-team", "/people"
    ];

    private static readonly Regex EmailRegex = new(
        @"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PhoneRegex = new(
        @"\+?\d[\d\s().\-]{7,}\d",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WebScraperService> _logger;

    public WebScraperService(IHttpClientFactory httpClientFactory, ILogger<WebScraperService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<CompanyCandidate> ScrapeCompanyAsync(WebSearchResult searchResult, string? locationHint, CancellationToken cancellationToken)
    {
        var domain = DomainNormalizer.ExtractDomain(searchResult.Url);
        var baseUri = domain is null ? null : new Uri($"https://{domain}");

        var candidate = new CompanyCandidate
        {
            Name = searchResult.Title,
            Website = baseUri?.ToString(),
            Domain = domain,
            Description = searchResult.Snippet,
            SourceUrl = searchResult.Url
        };

        if (baseUri is null)
            return candidate;

        var textChunks = new List<string>();
        var emails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var phones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var socialLinks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var logoCandidates = new List<string>();
        string? homepageHtml = null;

        foreach (var path in PagePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pageUrl = new Uri(baseUri, path).ToString();
            var html = await TryFetchHtmlAsync(pageUrl, cancellationToken);
            if (string.IsNullOrWhiteSpace(html))
                continue;

            if (path == string.Empty)
                homepageHtml = html.Length > 50000 ? html[..50000] : html;

            foreach (var (key, url) in SocialLinkExtractor.ExtractFromHtml(html))
            {
                if (!socialLinks.ContainsKey(key))
                    socialLinks[key] = url;
            }

            if (path == string.Empty)
                logoCandidates.AddRange(SocialLinkExtractor.ExtractLogoCandidates(html, baseUri));

            var text = ExtractCleanText(html);
            if (!string.IsNullOrWhiteSpace(text))
                textChunks.Add(text);

            foreach (Match match in EmailRegex.Matches(text))
                emails.Add(match.Value.ToLowerInvariant());

            foreach (Match match in PhoneRegex.Matches(text))
                phones.Add(match.Value);

            ExtractMailtoAndTel(html, emails, phones);
        }

        candidate.RawText = string.Join("\n\n", textChunks.Take(6));
        // Keep personal emails for contact matching, plus any domain emails that look personal.
        candidate.Emails = emails
            .Where(e => !GenericEmailFilter.IsGeneric(e))
            .Where(e => string.IsNullOrWhiteSpace(domain) || e.EndsWith($"@{domain}", StringComparison.OrdinalIgnoreCase)
                || LooksLikePersonalLocalPart(e))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToList();
        candidate.Phones = PhoneNormalizer.NormalizeMany(phones, locationHint).Take(10).ToList();
        candidate.PossiblePeopleNames = ExtractPossibleNames(candidate.RawText);
        candidate.JobTitles = ExtractJobTitles(candidate.RawText);
        candidate.HomepageHtml = homepageHtml;
        candidate.SocialLinks = socialLinks;
        candidate.LogoCandidateUrls = logoCandidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        return candidate;
    }

    private async Task<string?> TryFetchHtmlAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("WebScraper");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", "LeadSphereDiscoveryBot/1.0");

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                return null;

            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogDebug(ex, "Failed to fetch page {Url}", url);
            return null;
        }
    }

    private static string ExtractCleanText(string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);

        foreach (var node in document.DocumentNode.SelectNodes("//script|//style|//noscript|//svg") ?? Enumerable.Empty<HtmlNode>())
            node.Remove();

        var text = WebUtility.HtmlDecode(document.DocumentNode.InnerText);
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length > 12000 ? text[..12000] : text;
    }

    private static void ExtractMailtoAndTel(string html, HashSet<string> emails, HashSet<string> phones)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);

        foreach (var anchor in document.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>())
        {
            var href = WebUtility.HtmlDecode(anchor.GetAttributeValue("href", string.Empty)).Trim();
            if (href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            {
                var email = href["mailto:".Length..].Split('?', 2)[0].Trim().ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(email))
                    emails.Add(email);
            }
            else if (href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
            {
                var phone = href["tel:".Length..].Trim();
                if (!string.IsNullOrWhiteSpace(phone))
                    phones.Add(phone);
            }
        }
    }

    private static bool LooksLikePersonalLocalPart(string email)
    {
        var local = email.Split('@')[0];
        return local.Contains('.') || local.Contains('_') || local.Any(char.IsDigit) == false && local.Length >= 4;
    }

    private static List<string> ExtractPossibleNames(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var matches = Regex.Matches(text, @"\b([A-Z][a-z]+(?:\s+[A-Z][a-z]+){1,2})\b");
        return matches
            .Select(m => m.Value.Trim())
            .Where(n => !n.Contains("Inc", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(15)
            .ToList();
    }

    private static List<string> ExtractJobTitles(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        string[] titles =
        [
            "CEO", "CTO", "CFO", "COO", "CMO", "VP", "Director", "Head of", "Manager",
            "Founder", "Co-Founder", "President", "Chief"
        ];

        return titles
            .Where(t => text.Contains(t, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
