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

        var roleClause = string.Join(" OR ", DecisionQueries);
        var query = $"site:linkedin.com/in \"{cleanName}\" ({roleClause})";
        await CollectFromQueryAsync(query, linkedInCompanyUrl, contacts, seenUrls, maxResults: 10, cancellationToken);

        _logger.LogInformation("Discovered {Count} LinkedIn decision-makers for {Company}", contacts.Count, cleanName);
        return ContactQualityFilter.MergeAndRank(contacts, [], linkedInCompanyUrl).Take(targetCount).ToList();
    }

    private async Task CollectFromQueryAsync(
        string query,
        string? linkedInCompanyUrl,
        List<AiContactData> contacts,
        HashSet<string> seenUrls,
        int maxResults,
        CancellationToken cancellationToken)
    {
        try
        {
            var results = await _webSearch.SearchAsync(query, maxResults, context: null, cancellationToken);
            foreach (var contact in ParseResults(results, linkedInCompanyUrl))
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

    private static IEnumerable<AiContactData> ParseResults(IReadOnlyList<WebSearchResult> results, string? linkedInCompanyUrl)
    {
        foreach (var result in results)
        {
            var profileMatch = LinkedInProfileRegex.Match(result.Url);
            if (!profileMatch.Success)
                continue;

            var profileUrl = LinkedInContactUrl.NormalizePersonal(profileMatch.Value, linkedInCompanyUrl);
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

    private static string CleanCompanyName(string name)
    {
        name = Regex.Replace(name, @"\s[-|–].*$", string.Empty).Trim();
        name = Regex.Replace(name, @"\b(Inc|LLC|Ltd|S\.A\.|SA|Corp|Corporation|Company)\b\.?", "", RegexOptions.IgnoreCase).Trim();
        return name;
    }
}
