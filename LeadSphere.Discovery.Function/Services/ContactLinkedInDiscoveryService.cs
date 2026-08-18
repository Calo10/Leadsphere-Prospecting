using System.Text.RegularExpressions;
using LeadSphere.Discovery.Function.Infrastructure;
using LeadSphere.Discovery.Function.Models;
using Microsoft.Extensions.Logging;

namespace LeadSphere.Discovery.Function.Services;

public interface IContactLinkedInDiscoveryService
{
    Task ResolveMissingProfilesAsync(
        IList<AiContactData> contacts,
        string companyName,
        string? domain,
        CancellationToken cancellationToken);
}

public sealed class ContactLinkedInDiscoveryService : IContactLinkedInDiscoveryService
{
    private static readonly Regex LinkedInProfileRegex = new(
        @"https?://(?:[\w.-]+\.)?linkedin\.com/in/[\w%-]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TitleParseRegex = new(
        @"^(.+?)\s[-–|]\s(.+?)(?:\s[-–|]\s|$)",
        RegexOptions.Compiled);

    private readonly IWebSearchService _webSearch;
    private readonly ILogger<ContactLinkedInDiscoveryService> _logger;

    public ContactLinkedInDiscoveryService(
        IWebSearchService webSearch,
        ILogger<ContactLinkedInDiscoveryService> logger)
    {
        _webSearch = webSearch;
        _logger = logger;
    }

    public async Task ResolveMissingProfilesAsync(
        IList<AiContactData> contacts,
        string companyName,
        string? domain,
        CancellationToken cancellationToken)
    {
        var cleanCompany = CleanCompanyName(companyName);

        foreach (var contact in contacts)
        {
            contact.LinkedInUrl = LinkedInContactUrl.NormalizePersonal(contact.LinkedInUrl);
            if (LinkedInContactUrl.IsPersonalProfile(contact.LinkedInUrl))
                continue;

            contact.LinkedInUrl = null;

            var fullName = GetFullName(contact);
            if (string.IsNullOrWhiteSpace(fullName))
                continue;

            var profileUrl = await FindPersonalProfileAsync(fullName, cleanCompany, domain, cancellationToken);
            if (profileUrl is not null)
            {
                contact.LinkedInUrl = profileUrl;
                _logger.LogDebug("Resolved LinkedIn profile for {Name}: {Url}", fullName, profileUrl);
            }
        }
    }

    private async Task<string?> FindPersonalProfileAsync(
        string fullName,
        string companyName,
        string? domain,
        CancellationToken cancellationToken)
    {
        var queries = new List<string>
        {
            $"site:linkedin.com/in \"{fullName}\" \"{companyName}\""
        };

        if (!string.IsNullOrWhiteSpace(domain))
            queries.Add($"site:linkedin.com/in \"{fullName}\" {domain}");

        queries.Add($"site:linkedin.com/in \"{fullName}\"");

        foreach (var query in queries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var results = await _webSearch.SearchAsync(query, maxResults: 6, context: null, cancellationToken);
                var match = PickBestProfile(results, fullName, companyName);
                if (match is not null)
                    return match;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Contact LinkedIn search failed for {Name} with query {Query}", fullName, query);
            }
        }

        return null;
    }

    private static string? PickBestProfile(
        IReadOnlyList<WebSearchResult> results,
        string fullName,
        string companyName)
    {
        string? bestUrl = null;
        var bestScore = 0;

        foreach (var result in results)
        {
            var profileMatch = LinkedInProfileRegex.Match(result.Url);
            if (!profileMatch.Success)
                continue;

            var profileUrl = LinkedInContactUrl.NormalizePersonal(profileMatch.Value);
            if (profileUrl is null)
                continue;

            var (parsedName, _) = ParseLinkedInTitle(result.Title);
            var score = ScoreProfileMatch(fullName, companyName, parsedName, result.Title, result.Snippet);
            if (score > bestScore)
            {
                bestScore = score;
                bestUrl = profileUrl;
            }
        }

        return bestScore >= 2 ? bestUrl : null;
    }

    private static int ScoreProfileMatch(
        string expectedName,
        string companyName,
        string? parsedName,
        string title,
        string? snippet)
    {
        var score = 0;
        var haystack = $"{title} {snippet}".ToLowerInvariant();

        foreach (var token in TokenizeName(expectedName))
        {
            if (parsedName?.Contains(token, StringComparison.OrdinalIgnoreCase) == true)
                score += 2;
            else if (haystack.Contains(token, StringComparison.Ordinal))
                score += 1;
        }

        foreach (var token in TokenizeName(companyName))
        {
            if (token.Length > 2 && haystack.Contains(token, StringComparison.Ordinal))
                score += 1;
        }

        return score;
    }

    private static IEnumerable<string> TokenizeName(string value) =>
        value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.Length > 1)
            .Select(p => p.ToLowerInvariant());

    private static (string? FullName, string? JobTitle) ParseLinkedInTitle(string title)
    {
        title = title.Replace("| LinkedIn", "", StringComparison.OrdinalIgnoreCase).Trim();
        var match = TitleParseRegex.Match(title);
        if (!match.Success)
            return (null, null);

        var name = match.Groups[1].Value.Trim();
        var job = match.Groups[2].Value.Trim();
        return name.Length < 3 ? (null, null) : (name, job);
    }

    private static string GetFullName(AiContactData contact)
    {
        if (!string.IsNullOrWhiteSpace(contact.FullName))
            return contact.FullName.Trim();

        var parts = new[] { contact.FirstName, contact.LastName }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Trim());

        return string.Join(' ', parts);
    }

    private static string CleanCompanyName(string name)
    {
        name = Regex.Replace(name, @"\s[-|–].*$", string.Empty).Trim();
        name = Regex.Replace(name, @"\b(Inc|LLC|Ltd|S\.A\.|SA|Corp|Corporation|Company)\b\.?", "", RegexOptions.IgnoreCase).Trim();
        return name;
    }
}
