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
        {
            _logger.LogInformation(
                "Discovery job {JobId} is gone for search {SearchId}; treating as deleted and stopping.",
                message.JobId,
                message.SearchId);
            return;
        }

        var companiesInserted = 0;
        var contactsInserted = 0;

        if (await TryAbortIfCancelledAsync(message, companiesInserted, contactsInserted, cancellationToken))
            return;

        await _discoveryJobs.UpdateStatusAsync(message.OrgId, message.JobId, JobStatuses.Running, null, startedAt, null, cancellationToken);
        await _searches.UpdateStatusAsync(
            message.OrgId,
            message.SearchId,
            JobStatuses.Running,
            null,
            message.IsPretest ? null : startedAt,
            null,
            cancellationToken);

        if (await TryAbortIfCancelledAsync(message, companiesInserted, contactsInserted, cancellationToken))
            return;

        try
        {
            var search = await _searches.GetByIdAsync(message.OrgId, message.SearchId, cancellationToken);
            if (search is null
                || string.Equals(search.Status, JobStatuses.Cancelled, StringComparison.OrdinalIgnoreCase))
            {
                await TryAbortIfCancelledAsync(message, companiesInserted, contactsInserted, cancellationToken);
                return;
            }

            var searchIntent = SearchIntentResolver.Resolve(search);
            var feedback = search.FeedbackSignals;
            _logger.LogInformation(
                "Search {SearchId} feedback signals: notes={HasNotes} thumbsUp={ThumbsUp} thumbsDown={ThumbsDown}",
                message.SearchId,
                feedback?.HasNotes == true,
                feedback?.ThumbsUp.Count ?? 0,
                feedback?.ThumbsDown.Count ?? 0);

            var searchContext = new WebSearchContext
            {
                Location = searchIntent.SerpApiLocation,
                CountryCode = searchIntent.CountryCode,
                Language = searchIntent.Language
            };

            var locationHint = search.Criteria?.Location;
            var maxResults = message.IsPretest
                ? Math.Max(1, _options.PretestMaxResults)
                : _options.MaxCompaniesPerSearch;
            var maxContacts = message.IsPretest
                ? Math.Max(1, _options.PretestMaxResults)
                : _options.MaxContactsPerSearch;
            var maxQueries = message.IsPretest ? 1 : _options.MaxSearchQueries;
            var maxSerpResults = message.IsPretest
                ? Math.Min(8, _options.MaxResultsPerSearchQuery)
                : _options.MaxResultsPerSearchQuery;

            if (!search.TargetCompanies)
            {
                contactsInserted = await DiscoverContactsOnlyAsync(
                    message,
                    search,
                    searchContext,
                    locationHint,
                    maxContacts,
                    cancellationToken);
            }
            else
            {
            var queries = WebSearchQueryBuilder.BuildQueries(search, maxQueries);
            _logger.LogInformation(
                "Built {QueryCount} web search queries for search {SearchId}: {Queries}",
                queries.Count,
                message.SearchId,
                string.Join(" | ", queries));

            var allResults = new List<WebSearchResult>();
            foreach (var query in queries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await TryAbortIfCancelledAsync(message, companiesInserted, contactsInserted, cancellationToken))
                    return;

                var results = await _webSearch.SearchAsync(
                    query,
                    maxSerpResults,
                    searchContext,
                    cancellationToken);
                allResults.AddRange(results);
            }

            var uniqueResults = DomainNormalizer.DeduplicateByDomain(allResults);
            var relevantResults = SearchResultRelevanceFilter.FilterAndRank(search, uniqueResults, _options.MinIndustryRelevanceScore)
                .Take(maxResults)
                .ToList();

            _logger.LogInformation(
                "Filtered to {RelevantCount} industry-relevant companies (from {TotalCount}) for search {SearchId}",
                relevantResults.Count,
                uniqueResults.Count,
                message.SearchId);

            foreach (var result in relevantResults)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await TryAbortIfCancelledAsync(message, companiesInserted, contactsInserted, cancellationToken))
                    return;

                var domain = DomainNormalizer.ExtractDomain(result.Url);
                if (string.IsNullOrWhiteSpace(domain))
                    continue;

                if (feedback?.RejectsCompany(domain, result.Title) == true)
                {
                    _logger.LogInformation(
                        "Skipping company {Domain} — matches a thumbs-down example for search {SearchId}",
                        domain,
                        message.SearchId);
                    continue;
                }

                if (await _companies.ExistsByDomainAsync(message.OrgId, domain, cancellationToken))
                {
                    _logger.LogDebug("Skipping duplicate domain {Domain} for org {OrgId}", domain, message.OrgId);
                    continue;
                }

                var candidate = await _webScraper.ScrapeCompanyAsync(result, locationHint, cancellationToken);
                var enrichment = await _enrichment.EnrichAsync(candidate, cancellationToken);
                MergeEnrichmentIntoCandidate(candidate, enrichment);

                if (search.TargetContacts)
                {
                    var linkedInContacts = await _linkedInPeople.DiscoverDecisionMakersAsync(
                        candidate.Name,
                        domain,
                        enrichment.LinkedInUrl,
                        cancellationToken);
                    candidate.LinkedInContacts = ContactQualityFilter.MergeAndRank(
                        linkedInContacts,
                        candidate.WebsiteLinkedInContacts,
                        enrichment.LinkedInUrl);
                }

                var extraction = await _openAi.ExtractAsync(search, candidate, cancellationToken);

                if (extraction.Company is null || string.IsNullOrWhiteSpace(extraction.Company.Name))
                {
                    _logger.LogDebug("OpenAI returned no usable company for domain {Domain}", domain);
                    continue;
                }

                if (feedback?.RejectsCompany(extraction.Company.Domain ?? domain, extraction.Company.Name) == true)
                {
                    _logger.LogInformation(
                        "Skipping extracted company {Name} ({Domain}) — matches a thumbs-down example for search {SearchId}",
                        extraction.Company.Name,
                        extraction.Company.Domain ?? domain,
                        message.SearchId);
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

                Dictionary<string, EmailValidationResult> validationByEmail = new(StringComparer.OrdinalIgnoreCase);
                if (search.TargetContacts)
                {
                var qualityContacts = ContactQualityFilter.MergeAndRank(
                    candidate.LinkedInContacts,
                    extraction.Contacts,
                    enrichment.LinkedInUrl);
                NormalizeContactPhones(qualityContacts, locationHint);
                extraction.Contacts = qualityContacts;

                if (_options.EnableContactLinkedInWebSearch)
                {
                    await _contactLinkedIn.ResolveMissingProfilesAsync(
                        extraction.Contacts,
                        extraction.Company.Name,
                        domain,
                        enrichment.LinkedInUrl,
                        cancellationToken);
                }

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
                validationByEmail = emailValidations.ToDictionary(v => v.Email, StringComparer.OrdinalIgnoreCase);
                }

                var companyId = await _companies.InsertAsync(
                    message.OrgId,
                    message.SearchId,
                    extraction.Company,
                    extraction,
                    enrichment,
                    cancellationToken);
                companiesInserted++;

                var contactCount = 0;
                if (search.TargetContacts)
                {
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
                    if (feedback?.RejectsContact(contact.Email, contact.LinkedInUrl, contact.FullName) == true)
                    {
                        _logger.LogInformation(
                            "Skipping contact {Name} — matches a thumbs-down example for search {SearchId}",
                            contact.FullName,
                            message.SearchId);
                        continue;
                    }

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
                }

                _logger.LogInformation(
                    "Inserted company {CompanyName} ({Domain}) with {ContactCount} contacts",
                    extraction.Company.Name,
                    extraction.Company.Domain,
                    contactCount);

                if (companiesInserted >= maxResults)
                    break;
            }
            }

            if (await TryAbortIfCancelledAsync(message, companiesInserted, contactsInserted, cancellationToken))
                return;

            await FinishJobAsync(
                message,
                failed: false,
                errorMessage: null,
                companiesInserted,
                contactsInserted,
                cancellationToken);

            _logger.LogInformation(
                "Discovery job {JobId} completed. Pretest={Pretest} Companies={Companies} Contacts={Contacts}",
                message.JobId,
                message.IsPretest,
                companiesInserted,
                contactsInserted);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (await TryAbortIfCancelledAsync(message, companiesInserted, contactsInserted, cancellationToken))
                return;

            _logger.LogError(ex, "Discovery job {JobId} failed for search {SearchId}", message.JobId, message.SearchId);

            // Results already saved: complete the search so the UI is not "Failed" with companies/contacts.
            if (companiesInserted > 0 || contactsInserted > 0)
            {
                await FinishJobAsync(
                    message,
                    failed: false,
                    errorMessage: null,
                    companiesInserted,
                    contactsInserted,
                    cancellationToken);
                _logger.LogWarning(
                    ex,
                    "Discovery job {JobId} stopped after saving {Companies} companies and {Contacts} contacts; marking {Status}",
                    message.JobId,
                    companiesInserted,
                    contactsInserted,
                    message.IsPretest ? JobStatuses.Pending : JobStatuses.Completed);
                return;
            }

            var errorMessage = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
            await FinishJobAsync(
                message,
                failed: true,
                errorMessage,
                companiesInserted,
                contactsInserted,
                cancellationToken);
            if (!message.IsPretest)
                throw;
        }
    }

    private async Task<bool> TryAbortIfCancelledAsync(
        DiscoveryJobMessage message,
        int companiesInserted,
        int contactsInserted,
        CancellationToken cancellationToken)
    {
        if (!await _searches.IsCancelledAsync(message.OrgId, message.SearchId, cancellationToken))
            return false;

        var stoppedAt = DateTimeOffset.UtcNow;
        if (await _discoveryJobs.ExistsAsync(message.OrgId, message.JobId, message.SearchId, cancellationToken))
        {
            await _discoveryJobs.UpdateCountersAsync(message.OrgId, message.JobId, companiesInserted, contactsInserted, cancellationToken);
            await _discoveryJobs.UpdateStatusAsync(
                message.OrgId,
                message.JobId,
                JobStatuses.Cancelled,
                null,
                null,
                stoppedAt,
                cancellationToken);
        }

        _logger.LogInformation(
            "Discovery job {JobId} stopped because search {SearchId} was cancelled or deleted. Companies={Companies} Contacts={Contacts}",
            message.JobId,
            message.SearchId,
            companiesInserted,
            contactsInserted);
        return true;
    }

    private async Task FinishJobAsync(
        DiscoveryJobMessage message,
        bool failed,
        string? errorMessage,
        int companiesInserted,
        int contactsInserted,
        CancellationToken cancellationToken)
    {
        var finishedAt = DateTimeOffset.UtcNow;
        await _discoveryJobs.UpdateCountersAsync(message.OrgId, message.JobId, companiesInserted, contactsInserted, cancellationToken);
        await _searches.UpdateCountersAsync(message.OrgId, message.SearchId, companiesInserted, contactsInserted, cancellationToken);

        var jobStatus = failed ? JobStatuses.Failed : JobStatuses.Completed;
        await _discoveryJobs.UpdateStatusAsync(
            message.OrgId,
            message.JobId,
            jobStatus,
            errorMessage,
            null,
            finishedAt,
            cancellationToken);

        if (message.IsPretest)
        {
            await _searches.UpdateStatusAsync(
                message.OrgId,
                message.SearchId,
                JobStatuses.Pending,
                failed ? errorMessage : null,
                null,
                null,
                cancellationToken);
            return;
        }

        await _searches.UpdateStatusAsync(
            message.OrgId,
            message.SearchId,
            jobStatus,
            errorMessage,
            null,
            finishedAt,
            cancellationToken);
    }

    private async Task<int> DiscoverContactsOnlyAsync(
        DiscoveryJobMessage message,
        SearchRecord search,
        WebSearchContext searchContext,
        string? locationHint,
        int maxContacts,
        CancellationToken cancellationToken)
    {
        var people = (await _linkedInPeople.DiscoverMatchingPeopleAsync(
            search,
            searchContext,
            Math.Max(maxContacts, message.IsPretest ? Math.Min(10, maxContacts * 3) : maxContacts),
            cancellationToken)).ToList();

        await _openAi.ScoreContactsAsync(search, people, cancellationToken);

        var ranked = people
            .OrderByDescending(c => c.FitScore ?? 0)
            .ToList();

        var inserted = 0;
        foreach (var contact in ranked)
        {
            if (inserted >= maxContacts)
                break;

            cancellationToken.ThrowIfCancellationRequested();
            if (await TryAbortIfCancelledAsync(message, 0, inserted, cancellationToken))
                return inserted;

            if (search.FeedbackSignals?.RejectsContact(contact.Email, contact.LinkedInUrl, contact.FullName) == true)
            {
                _logger.LogInformation(
                    "Skipping contact {Name} — matches a thumbs-down example for search {SearchId}",
                    contact.FullName,
                    message.SearchId);
                continue;
            }

            if (contact.FitScore is { } score && score < _options.MinContactFitScore)
            {
                _logger.LogDebug(
                    "Skipping contact {Name} — fit score {FitScore} below minimum {MinFit}",
                    contact.FullName,
                    contact.FitScore,
                    _options.MinContactFitScore);
                continue;
            }

            if (await IsDuplicateContactAsync(message.OrgId, companyId: null, contact, cancellationToken))
                continue;

            if (!await _contacts.InsertAsync(
                    message.OrgId,
                    message.SearchId,
                    companyId: null,
                    contact,
                    emailValidation: null,
                    locationHint,
                    companyLinkedInUrl: null,
                    cancellationToken))
                continue;

            inserted++;
        }

        _logger.LogInformation(
            "Contact-only search {SearchId} inserted {Count} contacts",
            message.SearchId,
            inserted);
        return inserted;
    }

    private async Task<bool> IsDuplicateContactAsync(Guid orgId, Guid? companyId, AiContactData contact, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(contact.LinkedInUrl)
            && await _contacts.ExistsByLinkedInAsync(orgId, contact.LinkedInUrl.Trim(), cancellationToken))
            return true;

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
