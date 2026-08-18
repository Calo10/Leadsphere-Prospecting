using System.Text.RegularExpressions;
using LeadSphere.Discovery.Function.Infrastructure;
using LeadSphere.Discovery.Function.Models;
using LeadSphere.Discovery.Function.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeadSphere.Discovery.Function.Services;

public interface ILinkedInPeopleDiscoveryService
{
    Task<IReadOnlyList<AiContactData>> DiscoverDecisionMakersAsync(
        string companyName,
        string? domain,
        string? linkedInCompanyUrl,
        CancellationToken cancellationToken);
}

public sealed class LinkedInPeopleDiscoveryService : ILinkedInPeopleDiscoveryService
{
    private static readonly Regex LinkedInProfileRegex = new(
        @"https?://(?:[\w.-]+\.)?linkedin\.com/in/[\w%-]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TitleParseRegex = new(
        @"^(.+?)\s[-–|]\s(.+?)(?:\s[-–|]\s|$)",
        RegexOptions.Compiled);

    private static readonly Regex AtCompanyRegex = new(
        @"^(.+?)\s[-–|]\s(.+?)\s(?:at|@|en)\s",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] DecisionQueries =
    [
        "CEO OR Founder OR \"Co-Founder\" OR President OR Owner",
        "\"VP\" OR \"Vice President\" OR Director OR \"Head of\"",
        "CTO OR CFO OR CMO OR COO OR CRO OR \"Managing Director\"",
        "\"Head of Sales\" OR \"Sales Director\" OR \"Commercial Director\" OR \"Business Development\"",
        "\"General Manager\" OR \"Country Manager\" OR \"Regional Director\" OR Partner",
        "CHRO OR \"Head of HR\" OR \"Head of Marketing\" OR \"Head of Operations\""
    ];

    private readonly IWebSearchService _webSearch;
    private readonly DiscoveryOptions _options;
    private readonly ILogger<LinkedInPeopleDiscoveryService> _logger;

    public LinkedInPeopleDiscoveryService(
        IWebSearchService webSearch,
        IOptions<DiscoveryOptions> options,
        ILogger<LinkedInPeopleDiscoveryService> logger)
    {
        _webSearch = webSearch;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<AiContactData>> DiscoverDecisionMakersAsync(
        string companyName,
        string? domain,
        string? linkedInCompanyUrl,
        CancellationToken cancellationToken)
    {
        var contacts = new List<AiContactData>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cleanName = CleanCompanyName(companyName);
        var targetCount = Math.Min(_options.MaxContactsPerCompany, 12);

        foreach (var roleQuery in DecisionQueries.Take(_options.MaxLinkedInPeopleQueriesPerCompany))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var query = $"site:linkedin.com/in \"{cleanName}\" ({roleQuery})";
            await CollectFromQueryAsync(query, cleanName, contacts, seenUrls, maxResults: 8, cancellationToken);
        }

        var companySlug = ExtractLinkedInCompanySlug(linkedInCompanyUrl);
        if (!string.IsNullOrWhiteSpace(companySlug))
        {
            var companyQuery = $"site:linkedin.com/in {companySlug} (CEO OR Founder OR Director OR VP OR \"Head of\")";
            await CollectFromQueryAsync(companyQuery, cleanName, contacts, seenUrls, maxResults: 8, cancellationToken);
        }

        if (contacts.Count < targetCount && !string.IsNullOrWhiteSpace(domain))
        {
            var domainQuery = $"site:linkedin.com/in \"{domain}\" (CEO OR Founder OR Director OR VP OR \"Head of Sales\" OR CMO)";
            await CollectFromQueryAsync(domainQuery, cleanName, contacts, seenUrls, maxResults: 8, cancellationToken);
        }

        if (contacts.Count < targetCount)
        {
            var broadQuery = $"site:linkedin.com/in \"{cleanName}\" (executive OR leadership OR \"general manager\" OR partner)";
            await CollectFromQueryAsync(broadQuery, cleanName, contacts, seenUrls, maxResults: 6, cancellationToken);
        }

        _logger.LogInformation("Discovered {Count} LinkedIn decision-makers for {Company}", contacts.Count, cleanName);
        return ContactQualityFilter.MergeAndRank(contacts, []).Take(targetCount).ToList();
    }

    private async Task CollectFromQueryAsync(
        string query,
        string companyName,
        List<AiContactData> contacts,
        HashSet<string> seenUrls,
        int maxResults,
        CancellationToken cancellationToken)
    {
        try
        {
            var results = await _webSearch.SearchAsync(query, maxResults, context: null, cancellationToken);
            foreach (var contact in ParseResults(results, companyName))
            {
                if (string.IsNullOrWhiteSpace(contact.LinkedInUrl) || !seenUrls.Add(contact.LinkedInUrl))
                    continue;

                contacts.Add(contact);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LinkedIn people search failed for query {Query}", query);
        }
    }

    private static IEnumerable<AiContactData> ParseResults(IReadOnlyList<WebSearchResult> results, string companyName)
    {
        foreach (var result in results)
        {
            var profileMatch = LinkedInProfileRegex.Match(result.Url);
            if (!profileMatch.Success)
                continue;

            var profileUrl = LinkedInContactUrl.NormalizePersonal(profileMatch.Value);
            if (profileUrl is null)
                continue;
            var title = result.Title.Trim();

            var (fullName, jobTitle) = ParseLinkedInTitle(title);
            if (string.IsNullOrWhiteSpace(fullName))
                continue;

            if (!ContactQualityFilter.IsDecisionMakerTitle(jobTitle))
                continue;

            var parts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            yield return new AiContactData
            {
                FirstName = parts.Length > 0 ? parts[0] : null,
                LastName = parts.Length > 1 ? string.Join(' ', parts.Skip(1)) : null,
                FullName = fullName,
                JobTitle = jobTitle,
                LinkedInUrl = profileUrl
            };
        }
    }

    private static (string? FullName, string? JobTitle) ParseLinkedInTitle(string title)
    {
        title = title.Replace("| LinkedIn", "", StringComparison.OrdinalIgnoreCase).Trim();

        var match = TitleParseRegex.Match(title);
        if (match.Success)
        {
            var name = match.Groups[1].Value.Trim();
            var job = match.Groups[2].Value.Trim();
            if (IsValidParsedName(name))
                return (name, job);
        }

        var atMatch = AtCompanyRegex.Match(title);
        if (atMatch.Success)
        {
            var name = atMatch.Groups[1].Value.Trim();
            var job = atMatch.Groups[2].Value.Trim();
            if (IsValidParsedName(name))
                return (name, job);
        }

        return (null, null);
    }

    private static bool IsValidParsedName(string name) =>
        !name.Contains("LinkedIn", StringComparison.OrdinalIgnoreCase) && name.Length >= 3;

    private static string? ExtractLinkedInCompanySlug(string? linkedInCompanyUrl)
    {
        if (string.IsNullOrWhiteSpace(linkedInCompanyUrl))
            return null;

        var match = Regex.Match(
            linkedInCompanyUrl,
            @"linkedin\.com/company/([\w%-]+)",
            RegexOptions.IgnoreCase);

        return match.Success ? match.Groups[1].Value : null;
    }

    private static string CleanCompanyName(string name)
    {
        name = Regex.Replace(name, @"\s[-|–].*$", string.Empty).Trim();
        name = Regex.Replace(name, @"\b(Inc|LLC|Ltd|S\.A\.|SA|Corp|Corporation|Company)\b\.?", "", RegexOptions.IgnoreCase).Trim();
        return name;
    }
}
