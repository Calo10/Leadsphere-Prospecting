using LeadSphere.Discovery.Function.Models;
using LeadSphere.Discovery.Function.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeadSphere.Discovery.Function.Services;

public interface ICompanyEnrichmentService
{
    Task<CompanyEnrichmentData> EnrichAsync(CompanyCandidate candidate, CancellationToken cancellationToken);
    Task<IReadOnlyList<EmailValidationResult>> ValidateContactEmailsAsync(
        CompanyCandidate candidate,
        IEnumerable<AiContactData> contacts,
        CancellationToken cancellationToken);
}

public sealed class CompanyEnrichmentService : ICompanyEnrichmentService
{
    private readonly IWebSearchService _webSearch;
    private readonly ILogoResolutionService _logoResolution;
    private readonly IEmailValidationService _emailValidation;
    private readonly DiscoveryOptions _options;
    private readonly ILogger<CompanyEnrichmentService> _logger;

    public CompanyEnrichmentService(
        IWebSearchService webSearch,
        ILogoResolutionService logoResolution,
        IEmailValidationService emailValidation,
        IOptions<DiscoveryOptions> options,
        ILogger<CompanyEnrichmentService> logger)
    {
        _webSearch = webSearch;
        _logoResolution = logoResolution;
        _emailValidation = emailValidation;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<CompanyEnrichmentData> EnrichAsync(CompanyCandidate candidate, CancellationToken cancellationToken)
    {
        var enrichment = new CompanyEnrichmentData
        {
            LinkedInUrl = candidate.SocialLinks.GetValueOrDefault("linkedin"),
            TwitterUrl = candidate.SocialLinks.GetValueOrDefault("twitter"),
            FacebookUrl = candidate.SocialLinks.GetValueOrDefault("facebook"),
            InstagramUrl = candidate.SocialLinks.GetValueOrDefault("instagram"),
            CrunchbaseUrl = candidate.SocialLinks.GetValueOrDefault("crunchbase")
        };

        if (candidate.SocialLinks.Count > 0)
            enrichment.EnrichmentSources.Add("website");

        enrichment.LogoUrl = await _logoResolution.ResolveAsync(candidate, cancellationToken);
        if (!string.IsNullOrWhiteSpace(enrichment.LogoUrl))
            enrichment.EnrichmentSources.Add("logo");

        if (_options.EnableExternalProfileSearch)
            await EnrichFromWebSearchAsync(candidate, enrichment, cancellationToken);

        return enrichment;
    }

    public Task<IReadOnlyList<EmailValidationResult>> ValidateContactEmailsAsync(
        CompanyCandidate candidate,
        IEnumerable<AiContactData> contacts,
        CancellationToken cancellationToken)
    {
        var emails = candidate.Emails
            .Concat(contacts.Select(c => c.Email).Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e!));

        return _emailValidation.ValidateManyAsync(emails, cancellationToken);
    }

    private async Task EnrichFromWebSearchAsync(
        CompanyCandidate candidate,
        CompanyEnrichmentData enrichment,
        CancellationToken cancellationToken)
    {
        var queries = new List<(string Platform, string Query, Action<string> Apply)>();
        var name = candidate.Name.Trim();

        if (string.IsNullOrWhiteSpace(enrichment.LinkedInUrl))
            queries.Add(("linkedin", $"\"{name}\" site:linkedin.com/company", url => enrichment.LinkedInUrl = url));

        if (string.IsNullOrWhiteSpace(enrichment.CrunchbaseUrl))
            queries.Add(("crunchbase", $"\"{name}\" site:crunchbase.com/organization", url => enrichment.CrunchbaseUrl = url));

        if (!string.IsNullOrWhiteSpace(candidate.Domain))
        {
            if (string.IsNullOrWhiteSpace(enrichment.CrunchbaseUrl))
                queries.Add(("crunchbase", $"{candidate.Domain} site:crunchbase.com/organization", url => enrichment.CrunchbaseUrl ??= url));
        }

        var executed = 0;
        foreach (var (platform, query, apply) in queries.Take(_options.MaxEnrichmentQueriesPerCompany))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var results = await _webSearch.SearchAsync(query, maxResults: 5, context: null, cancellationToken);
                var match = results.FirstOrDefault(r => MatchesPlatform(platform, r.Url));
                if (match is null)
                    continue;

                apply(NormalizeProfileUrl(match.Url));
                enrichment.EnrichmentSources.Add($"serpapi:{platform}");
                executed++;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "External profile search failed for {Platform} / {Company}", platform, name);
            }
        }

        _logger.LogDebug("External enrichment for {Company}: {Count} profile queries", name, executed);
    }

    private static bool MatchesPlatform(string platform, string url) =>
        platform switch
        {
            "linkedin" => url.Contains("linkedin.com/company/", StringComparison.OrdinalIgnoreCase),
            "crunchbase" => url.Contains("crunchbase.com/organization/", StringComparison.OrdinalIgnoreCase),
            _ => false
        };

    private static string NormalizeProfileUrl(string url)
    {
        url = url.Trim().TrimEnd('/');
        var q = url.IndexOf('?', StringComparison.Ordinal);
        return q > 0 ? url[..q] : url;
    }
}
