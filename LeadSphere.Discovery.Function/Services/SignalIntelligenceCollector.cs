using LeadSphere.Discovery.Function.Models;
using LeadSphere.Discovery.Function.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeadSphere.Discovery.Function.Services;

public interface ISignalIntelligenceCollector
{
    Task<IReadOnlyList<SignalSnapshotNewsItem>> CollectAsync(
        string companyName,
        string? location,
        CancellationToken cancellationToken);
}

public sealed class SignalIntelligenceCollector : ISignalIntelligenceCollector
{
    private static readonly (string Kind, string Query)[] Themes =
    [
        ("funding", "(funding OR raised OR \"series A\" OR \"series B\" OR \"series C\" OR investment OR investor OR \"venture capital\" OR acquired OR acquisition OR IPO OR merger)"),
        ("hiring", "(hiring OR \"is hiring\" OR \"we're hiring\" OR \"open roles\" OR recruiting OR \"job openings\" OR headcount OR \"talent acquisition\")"),
        ("hiring", "site:linkedin.com/jobs OR site:lever.co OR site:greenhouse.io OR site:ashbyhq.com"),
        ("hiring", "(layoff OR layoffs OR \"job cuts\" OR restructuring OR downsizing OR \"reducing headcount\")"),
        ("leadership", "(appointed OR \"named CEO\" OR \"named CFO\" OR \"joins as\" OR promoted OR \"new CEO\" OR \"new CTO\" OR \"new COO\" OR \"steps down\" OR \"chief executive\")"),
        ("expansion", "(expansion OR \"new office\" OR \"opens office\" OR launch OR \"product launch\" OR partnership OR \"market entry\" OR \"opens in\")"),
        ("funding", "site:crunchbase.com (funding OR news OR acquisition)"),
        ("coverage", "(\"press release\" OR announces OR announced)")
    ];

    private readonly IWebSearchService _webSearch;
    private readonly SignalEvaluationOptions _options;
    private readonly ILogger<SignalIntelligenceCollector> _logger;

    public SignalIntelligenceCollector(
        IWebSearchService webSearch,
        IOptions<SignalEvaluationOptions> options,
        ILogger<SignalIntelligenceCollector> logger)
    {
        _webSearch = webSearch;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SignalSnapshotNewsItem>> CollectAsync(
        string companyName,
        string? location,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(companyName))
            return [];

        var context = string.IsNullOrWhiteSpace(location)
            ? null
            : new WebSearchContext { Location = location };
        var take = Math.Clamp(_options.MaxResultsPerQuery, 1, 10);
        var items = new List<SignalSnapshotNewsItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var theme in Themes.Take(Math.Clamp(_options.MaxSearchQueries, 1, Themes.Length)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = $"\"{companyName}\" {theme.Query}";
            IReadOnlyList<WebSearchResult> results;
            try
            {
                results = await _webSearch.SearchAsync(query, take, context, cancellationToken, countAgainstBudget: false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SerpAPI signal search failed for {Company} ({Kind})", companyName, theme.Kind);
                continue;
            }

            foreach (var result in results)
            {
                if (string.IsNullOrWhiteSpace(result.Title) || !seen.Add(result.Title))
                    continue;

                items.Add(new SignalSnapshotNewsItem
                {
                    Title = result.Title.Trim(),
                    Url = string.IsNullOrWhiteSpace(result.Url) ? null : result.Url,
                    Source = result.Domain,
                    Snippet = result.Snippet,
                    Kind = theme.Kind
                });
            }
        }

        return items.Take(20).ToList();
    }
}
