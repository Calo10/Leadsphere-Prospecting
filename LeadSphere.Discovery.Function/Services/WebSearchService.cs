using System.Text.Json;
using LeadSphere.Discovery.Function.Models;
using LeadSphere.Discovery.Function.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeadSphere.Discovery.Function.Services;

public interface IWebSearchService
{
    Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query,
        int maxResults,
        WebSearchContext? context,
        CancellationToken cancellationToken,
        bool countAgainstBudget = true);
}

public sealed class WebSearchService : IWebSearchService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly WebSearchOptions _options;
    private readonly DiscoveryOptions _discovery;
    private readonly ILogger<WebSearchService> _logger;
    private int _calls;

    public WebSearchService(
        IHttpClientFactory httpClientFactory,
        IOptions<WebSearchOptions> options,
        IOptions<DiscoveryOptions> discovery,
        ILogger<WebSearchService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _discovery = discovery.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query,
        int maxResults,
        WebSearchContext? context,
        CancellationToken cancellationToken,
        bool countAgainstBudget = true)
    {
        if (countAgainstBudget)
        {
            var budget = Math.Max(0, _discovery.MaxWebSearchCallsPerSearch);
            var used = Interlocked.Increment(ref _calls);
            if (budget > 0 && used > budget)
            {
                if (used == budget + 1)
                {
                    _logger.LogWarning(
                        "Web search budget reached ({Budget} calls) for this discovery job; skipping remaining queries",
                        budget);
                }

                return [];
            }
        }

        var provider = _options.Provider.Trim();
        return provider.ToLowerInvariant() switch
        {
            "serpapi" => await SearchSerpApiAsync(query, maxResults, context, cancellationToken),
            "google" => await SearchGoogleAsync(query, maxResults, context, cancellationToken),
            "bing" => await SearchBingAsync(query, maxResults, context, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported web search provider '{provider}'.")
        };
    }

    private async Task<IReadOnlyList<WebSearchResult>> SearchSerpApiAsync(
        string query,
        int maxResults,
        WebSearchContext? context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.SerpApi.ApiKey))
            throw new InvalidOperationException("WebSearch:SerpApi:ApiKey is not configured.");

        var num = Math.Clamp(maxResults, 1, 20);
        var url =
            $"https://serpapi.com/search.json?engine=google&q={Uri.EscapeDataString(query)}&num={num}&api_key={Uri.EscapeDataString(_options.SerpApi.ApiKey)}";

        if (!string.IsNullOrWhiteSpace(context?.Location))
            url += $"&location={Uri.EscapeDataString(context.Location)}";
        if (!string.IsNullOrWhiteSpace(context?.CountryCode))
            url += $"&gl={Uri.EscapeDataString(context.CountryCode)}";
        if (!string.IsNullOrWhiteSpace(context?.Language))
            url += $"&hl={Uri.EscapeDataString(context.Language)}";

        using var response = await CreateClient().GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("organic_results", out var organic))
            return [];

        return organic.EnumerateArray()
            .Take(maxResults)
            .Select(item => new WebSearchResult
            {
                Title = item.TryGetProperty("title", out var title) ? title.GetString() ?? string.Empty : string.Empty,
                Url = item.TryGetProperty("link", out var link) ? link.GetString() ?? string.Empty : string.Empty,
                Snippet = item.TryGetProperty("snippet", out var snippet) ? snippet.GetString() : null,
                Domain = item.TryGetProperty("displayed_link", out var domain) ? domain.GetString() : null
            })
            .Where(r => !string.IsNullOrWhiteSpace(r.Url) && !DomainNormalizer.IsBlockedUrl(r.Url))
            .ToList();
    }

    private async Task<IReadOnlyList<WebSearchResult>> SearchGoogleAsync(
        string query,
        int maxResults,
        WebSearchContext? context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Google.ApiKey) || string.IsNullOrWhiteSpace(_options.Google.SearchEngineId))
            throw new InvalidOperationException("WebSearch:Google ApiKey and SearchEngineId must be configured.");

        var url = $"https://www.googleapis.com/customsearch/v1?key={Uri.EscapeDataString(_options.Google.ApiKey)}&cx={Uri.EscapeDataString(_options.Google.SearchEngineId)}&q={Uri.EscapeDataString(query)}&num={Math.Min(maxResults, 10)}";
        using var response = await CreateClient().GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("items", out var items))
            return [];

        return items.EnumerateArray()
            .Take(maxResults)
            .Select(item => new WebSearchResult
            {
                Title = item.TryGetProperty("title", out var title) ? title.GetString() ?? string.Empty : string.Empty,
                Url = item.TryGetProperty("link", out var link) ? link.GetString() ?? string.Empty : string.Empty,
                Snippet = item.TryGetProperty("snippet", out var snippet) ? snippet.GetString() : null,
                Domain = item.TryGetProperty("displayLink", out var domain) ? domain.GetString() : null
            })
            .Where(r => !string.IsNullOrWhiteSpace(r.Url) && !DomainNormalizer.IsBlockedUrl(r.Url))
            .ToList();
    }

    private async Task<IReadOnlyList<WebSearchResult>> SearchBingAsync(
        string query,
        int maxResults,
        WebSearchContext? context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Bing.ApiKey))
            throw new InvalidOperationException("WebSearch:Bing:ApiKey is not configured.");

        var client = CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", _options.Bing.ApiKey);

        var url = $"https://api.bing.microsoft.com/v7.0/search?q={Uri.EscapeDataString(query)}&count={maxResults}";
        using var response = await client.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("webPages", out var webPages) ||
            !webPages.TryGetProperty("value", out var values))
            return [];

        return values.EnumerateArray()
            .Take(maxResults)
            .Select(item => new WebSearchResult
            {
                Title = item.TryGetProperty("name", out var title) ? title.GetString() ?? string.Empty : string.Empty,
                Url = item.TryGetProperty("url", out var link) ? link.GetString() ?? string.Empty : string.Empty,
                Snippet = item.TryGetProperty("snippet", out var snippet) ? snippet.GetString() : null,
                Domain = item.TryGetProperty("displayUrl", out var domain) ? domain.GetString() : null
            })
            .Where(r => !string.IsNullOrWhiteSpace(r.Url) && !DomainNormalizer.IsBlockedUrl(r.Url))
            .ToList();
    }

    private HttpClient CreateClient() => _httpClientFactory.CreateClient("WebSearch");
}

public static class WebSearchQueryBuilder
{
    public static IReadOnlyList<string> BuildQueries(SearchRecord search, int maxQueries = 6)
    {
        var intent = SearchIntentResolver.Resolve(search);
        var queries = new List<string>();
        var industry = intent.Industry;
        var location = intent.Location;
        var profile = TrimProfile(intent.Profile);

        if (!string.IsNullOrWhiteSpace(location))
            queries.Add($"{industry} en {location}");

        queries.Add($"{industry} {location}".Trim());

        if (!string.IsNullOrWhiteSpace(profile) && profile.Length <= 120)
            queries.Add(profile);

        foreach (var englishVariant in SearchIntentResolver.EnglishIndustryVariants(industry))
        {
            if (!string.IsNullOrWhiteSpace(location))
                queries.Add($"{englishVariant} {location}");
        }

        if (!string.IsNullOrWhiteSpace(location))
            queries.Add($"{industry} empresa {location} servicios");

        queries.Add($"{industry} company {location} -jobs -wikipedia".Trim());

        return queries
            .Select(q => q.Trim())
            .Where(q => q.Length >= 8)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxQueries)
            .ToList();
    }

    private static string TrimProfile(string profile)
    {
        profile = profile.Trim();
        return profile.Length > 120 ? profile[..120] : profile;
    }
}

public static class DomainNormalizer
{
    private static readonly HashSet<string> DirectoryHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "linkedin.com", "facebook.com", "twitter.com", "x.com", "instagram.com",
        "youtube.com", "wikipedia.org", "crunchbase.com", "glassdoor.com",
        "indeed.com", "yelp.com", "yellowpages.com", "bbb.org", "mapquest.com",
        "tripadvisor.com"
    };

    private static readonly string[] BlockedHostSuffixes = [".gov", ".mil", ".edu"];

    public static bool IsBlockedUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return true;

        return IsBlockedHost(uri.Host);
    }

    public static bool IsDirectoryOrSocialUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && IsDirectoryOrSocialHost(uri.Host);

    public static bool IsBlockedHost(string host)
    {
        host = NormalizeHost(host);

        if (host.Contains(".gov", StringComparison.Ordinal) || host.EndsWith(".gov", StringComparison.Ordinal))
            return true;

        return BlockedHostSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.Ordinal));
    }

    public static bool IsDirectoryOrSocialHost(string host)
    {
        host = NormalizeHost(host);
        if (DirectoryHosts.Contains(host))
            return true;

        return host.EndsWith(".linkedin.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".facebook.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".instagram.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".crunchbase.com", StringComparison.OrdinalIgnoreCase);
    }

    public static string? ExtractDomain(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        var host = NormalizeHost(uri.Host);
        if (IsDirectoryOrSocialHost(host) || IsBlockedHost(host))
            return null;

        return host;
    }

    public static IReadOnlyList<WebSearchResult> DeduplicateByDomain(IEnumerable<WebSearchResult> results)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var output = new List<WebSearchResult>();

        foreach (var result in results)
        {
            if (IsDirectoryOrSocialUrl(result.Url) || IsBlockedUrl(result.Url))
                continue;

            var domain = ExtractDomain(result.Url);
            if (string.IsNullOrWhiteSpace(domain) && !string.IsNullOrWhiteSpace(result.Domain))
            {
                domain = NormalizeHost(result.Domain);
                if (IsDirectoryOrSocialHost(domain) || IsBlockedHost(domain))
                    domain = null;
            }

            if (string.IsNullOrWhiteSpace(domain))
                continue;

            if (!seen.Add(domain))
                continue;

            result.Domain = domain;
            output.Add(result);
        }

        return output;
    }

    private static string NormalizeHost(string host)
    {
        host = host.Trim().ToLowerInvariant();
        if (host.StartsWith("www."))
            host = host[4..];
        return host;
    }
}
