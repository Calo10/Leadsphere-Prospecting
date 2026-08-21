using LeadSphere.Discovery.Function.Constants;
using LeadSphere.Discovery.Function.Infrastructure;
using LeadSphere.Discovery.Function.Models;
using LeadSphere.Discovery.Function.Options;
using LeadSphere.Discovery.Function.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeadSphere.Discovery.Function.Services;

public interface IDiscoveryService
{
    Task ProcessJobAsync(DiscoveryJobMessage message, CancellationToken cancellationToken);
}

public sealed class DiscoveryService : IDiscoveryService
{
    private readonly IDiscoveryJobRepository _discoveryJobs;
    private readonly ISearchRepository _searches;
    private readonly ICompanyRepository _companies;
    private readonly IContactRepository _contacts;
    private readonly IWebSearchService _webSearch;
    private readonly IWebScraperService _webScraper;
    private readonly ICompanyEnrichmentService _enrichment;
    private readonly ILinkedInPeopleDiscoveryService _linkedInPeople;
    private readonly IContactLinkedInDiscoveryService _contactLinkedIn;
    private readonly IContactDataEnrichmentService _contactData;
    private readonly ICompanyMarketDataService _marketData;
    private readonly IOpenAiExtractionService _openAi;
    private readonly DiscoveryOptions _options;
    private readonly ILogger<DiscoveryService> _logger;

    public DiscoveryService(
        IDiscoveryJobRepository discoveryJobs,
        ISearchRepository searches,
        ICompanyRepository companies,
        IContactRepository contacts,
        IWebSearchService webSearch,
        IWebScraperService webScraper,
        ICompanyEnrichmentService enrichment,
        ILinkedInPeopleDiscoveryService linkedInPeople,
        IContactLinkedInDiscoveryService contactLinkedIn,
        IContactDataEnrichmentService contactData,
        ICompanyMarketDataService marketData,
        IOpenAiExtractionService openAi,
        IOptions<DiscoveryOptions> options,
        ILogger<DiscoveryService> logger)
    {
        _discoveryJobs = discoveryJobs;
        _searches = searches;
        _companies = companies;
        _contacts = contacts;
        _webSearch = webSearch;
        _webScraper = webScraper;
        _enrichment = enrichment;
        _linkedInPeople = linkedInPeople;
        _contactLinkedIn = contactLinkedIn;
        _contactData = contactData;
        _marketData = marketData;
        _openAi = openAi;
        _options = options.Value;
        _logger = logger;
    }

    public async Task ProcessJobAsync(DiscoveryJobMessage message, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;

        var jobExists = await _discoveryJobs.ExistsAsync(message.OrgId, message.JobId, message.SearchId, cancellationToken);
        if (!jobExists)
            throw new InvalidOperationException($"Discovery job {message.JobId} was not found for search {message.SearchId}.");

        await _discoveryJobs.UpdateStatusAsync(message.OrgId, message.JobId, JobStatuses.Running, null, startedAt, null, cancellationToken);
        await _searches.UpdateStatusAsync(message.OrgId, message.SearchId, JobStatuses.Running, null, startedAt, null, cancellationToken);

        var companiesInserted = 0;
        var contactsInserted = 0;

        try
        {
            var search = await _searches.GetByIdAsync(message.OrgId, message.SearchId, cancellationToken)
                ?? throw new InvalidOperationException($"Search {message.SearchId} was not found.");

            var searchIntent = SearchIntentResolver.Resolve(search);
            var searchContext = new WebSearchContext
            {
                Location = searchIntent.SerpApiLocation,
                CountryCode = searchIntent.CountryCode,
                Language = searchIntent.Language
            };

            var queries = WebSearchQueryBuilder.BuildQueries(search, _options.MaxSearchQueries);
            _logger.LogInformation(
                "Built {QueryCount} web search queries for search {SearchId}: {Queries}",
                queries.Count,
                message.SearchId,
                string.Join(" | ", queries));

            var allResults = new List<WebSearchResult>();
            foreach (var query in queries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var results = await _webSearch.SearchAsync(
                    query,
                    _options.MaxResultsPerSearchQuery,
                    searchContext,
                    cancellationToken);
                allResults.AddRange(results);
            }

            var uniqueResults = DomainNormalizer.DeduplicateByDomain(allResults);
            var relevantResults = SearchResultRelevanceFilter.FilterAndRank(search, uniqueResults, _options.MinIndustryRelevanceScore)
                .Take(_options.MaxCompaniesPerSearch)
                .ToList();

            _logger.LogInformation(
                "Filtered to {RelevantCount} industry-relevant companies (from {TotalCount}) for search {SearchId}",
                relevantResults.Count,
                uniqueResults.Count,
                message.SearchId);

            var locationHint = search.Criteria?.Location;

            foreach (var result in relevantResults)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var domain = DomainNormalizer.ExtractDomain(result.Url);
                if (string.IsNullOrWhiteSpace(domain))
                    continue;

                if (await _companies.ExistsByDomainAsync(message.OrgId, domain, cancellationToken))
                {
                    _logger.LogDebug("Skipping duplicate domain {Domain} for org {OrgId}", domain, message.OrgId);
                    continue;
                }

                var candidate = await _webScraper.ScrapeCompanyAsync(result, locationHint, cancellationToken);
                var enrichment = await _enrichment.EnrichAsync(candidate, cancellationToken);
                MergeEnrichmentIntoCandidate(candidate, enrichment);

                var linkedInContacts = await _linkedInPeople.DiscoverDecisionMakersAsync(
                    candidate.Name,
                    domain,
                    enrichment.LinkedInUrl,
                    cancellationToken);
                candidate.LinkedInContacts = ContactQualityFilter.MergeAndRank(
                    linkedInContacts,
                    candidate.WebsiteLinkedInContacts,
                    enrichment.LinkedInUrl);

                var extraction = await _openAi.ExtractAsync(search, candidate, cancellationToken);

                if (extraction.Company is null || string.IsNullOrWhiteSpace(extraction.Company.Name))
                {
                    _logger.LogDebug("OpenAI returned no usable company for domain {Domain}", domain);
                    continue;
                }

                if ((extraction.FitScore ?? 0) < _options.MinCompanyFitScore)
                {
                    _logger.LogDebug(
                        "Skipping company {Domain} — fit score {FitScore} below minimum {MinFit}",
                        domain,
                        extraction.FitScore,
                        _options.MinCompanyFitScore);
                    continue;
                }

                extraction.Company.Domain ??= domain;
                extraction.Company.Website ??= candidate.Website;

                if (await _companies.ExistsByDomainAsync(message.OrgId, extraction.Company.Domain, cancellationToken))
                    continue;

                await _marketData.ApplyAsync(
                    enrichment,
                    extraction.Company.Name,
                    extraction.Company.Domain,
                    searchIntent.CountryCode,
                    searchIntent.Language,
                    cancellationToken);

                var qualityContacts = ContactQualityFilter.MergeAndRank(
                    candidate.LinkedInContacts,
                    extraction.Contacts,
                    enrichment.LinkedInUrl);
                NormalizeContactPhones(qualityContacts, locationHint);
                extraction.Contacts = qualityContacts;

                await _contactLinkedIn.ResolveMissingProfilesAsync(
                    extraction.Contacts,
                    extraction.Company.Name,
                    domain,
                    enrichment.LinkedInUrl,
                    cancellationToken);

                await _contactData.EnrichAsync(
                    extraction.Contacts,
                    candidate,
                    extraction.Company.Name,
                    locationHint,
                    cancellationToken);

                extraction.Contacts = extraction.Contacts
                    .OrderByDescending(ContactQualityFilter.ContactReachabilityScore)
                    .ThenByDescending(c => ContactQualityFilter.IsDecisionMakerTitle(c.JobTitle) ? 1 : 0)
                    .ToList();

                var emailValidations = await _enrichment.ValidateContactEmailsAsync(candidate, extraction.Contacts, cancellationToken);
                enrichment.EmailValidations = emailValidations.ToList();
                var validationByEmail = emailValidations.ToDictionary(v => v.Email, StringComparer.OrdinalIgnoreCase);

                var companyId = await _companies.InsertAsync(
                    message.OrgId,
                    message.SearchId,
                    extraction.Company,
                    extraction,
                    enrichment,
                    cancellationToken);
                companiesInserted++;

                var contactCount = 0;
                var contactsToInsert = extraction.Contacts
                    .Where(ContactQualityFilter.IsQualityContact)
                    .Where(c => string.IsNullOrWhiteSpace(c.Email) || !GenericEmailFilter.IsGeneric(c.Email))
                    .OrderByDescending(ContactQualityFilter.ContactReachabilityScore)
                    .ToList();

                if (_options.PreferContactsWithEmailOrPhone)
                {
                    var reachable = contactsToInsert.Where(ContactQualityFilter.HasReachableChannel).ToList();
                    if (reachable.Count > 0)
                        contactsToInsert = reachable;
                }

                foreach (var contact in contactsToInsert.Take(_options.MaxContactsPerCompany))
                {
                    if (await IsDuplicateContactAsync(message.OrgId, companyId, contact, cancellationToken))
                        continue;

                    EmailValidationResult? emailValidation = null;
                    if (!string.IsNullOrWhiteSpace(contact.Email)
                        && validationByEmail.TryGetValue(contact.Email.Trim().ToLowerInvariant(), out var validation))
                    {
                        emailValidation = validation;
                        if (validation.Status is "invalid" or "disposable")
                            continue;
                    }

                    if (!await _contacts.InsertAsync(
                        message.OrgId,
                        message.SearchId,
                        companyId,
                        contact,
                        emailValidation,
                        locationHint,
                        enrichment.LinkedInUrl,
                        cancellationToken))
                        continue;

                    contactsInserted++;
                    contactCount++;
                }

                _logger.LogInformation(
                    "Inserted company {CompanyName} ({Domain}) with {ContactCount} contacts",
                    extraction.Company.Name,
                    extraction.Company.Domain,
                    contactCount);
            }

            var completedAt = DateTimeOffset.UtcNow;
            await _discoveryJobs.UpdateCountersAsync(message.OrgId, message.JobId, companiesInserted, contactsInserted, cancellationToken);
            await _searches.UpdateCountersAsync(message.OrgId, message.SearchId, companiesInserted, contactsInserted, cancellationToken);
            await _discoveryJobs.UpdateStatusAsync(message.OrgId, message.JobId, JobStatuses.Completed, null, null, completedAt, cancellationToken);
            await _searches.UpdateStatusAsync(message.OrgId, message.SearchId, JobStatuses.Completed, null, null, completedAt, cancellationToken);

            _logger.LogInformation(
                "Discovery job {JobId} completed. Companies={Companies} Contacts={Contacts}",
                message.JobId,
                companiesInserted,
                contactsInserted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Discovery job {JobId} failed for search {SearchId}", message.JobId, message.SearchId);

            // Results already saved: complete the search so the UI is not "Failed" with companies/contacts.
            if (companiesInserted > 0 || contactsInserted > 0)
            {
                var completedAt = DateTimeOffset.UtcNow;
                await _discoveryJobs.UpdateCountersAsync(message.OrgId, message.JobId, companiesInserted, contactsInserted, cancellationToken);
                await _searches.UpdateCountersAsync(message.OrgId, message.SearchId, companiesInserted, contactsInserted, cancellationToken);
                await _discoveryJobs.UpdateStatusAsync(message.OrgId, message.JobId, JobStatuses.Completed, null, null, completedAt, cancellationToken);
                await _searches.UpdateStatusAsync(message.OrgId, message.SearchId, JobStatuses.Completed, null, null, completedAt, cancellationToken);
                _logger.LogWarning(
                    ex,
                    "Discovery job {JobId} stopped after saving {Companies} companies and {Contacts} contacts; marking completed",
                    message.JobId,
                    companiesInserted,
                    contactsInserted);
                return;
            }

            var errorMessage = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
            var failedAt = DateTimeOffset.UtcNow;

            await _discoveryJobs.UpdateStatusAsync(message.OrgId, message.JobId, JobStatuses.Failed, errorMessage, null, failedAt, cancellationToken);
            await _searches.UpdateStatusAsync(message.OrgId, message.SearchId, JobStatuses.Failed, errorMessage, null, failedAt, cancellationToken);
            throw;
        }
    }

    private async Task<bool> IsDuplicateContactAsync(Guid orgId, Guid companyId, AiContactData contact, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(contact.Email))
            return await _contacts.ExistsByEmailAsync(orgId, contact.Email.Trim(), cancellationToken);

        var fullName = !string.IsNullOrWhiteSpace(contact.FullName)
            ? contact.FullName.Trim()
            : string.Join(' ', new[] { contact.FirstName, contact.LastName }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();

        if (string.IsNullOrWhiteSpace(fullName))
            return false;

        return await _contacts.ExistsByNameAsync(orgId, companyId, fullName, cancellationToken);
    }

    private static void MergeEnrichmentIntoCandidate(CompanyCandidate candidate, CompanyEnrichmentData enrichment)
    {
        void SetSocial(string key, string? url)
        {
            if (!string.IsNullOrWhiteSpace(url) && !candidate.SocialLinks.ContainsKey(key))
                candidate.SocialLinks[key] = url;
        }

        SetSocial("linkedin", enrichment.LinkedInUrl);
        SetSocial("twitter", enrichment.TwitterUrl);
        SetSocial("facebook", enrichment.FacebookUrl);
        SetSocial("instagram", enrichment.InstagramUrl);
        SetSocial("crunchbase", enrichment.CrunchbaseUrl);
    }

    private static void NormalizeContactPhones(IEnumerable<AiContactData> contacts, string? locationHint)
    {
        foreach (var contact in contacts)
            contact.Phone = PhoneNormalizer.Normalize(contact.Phone, locationHint);
    }
}
