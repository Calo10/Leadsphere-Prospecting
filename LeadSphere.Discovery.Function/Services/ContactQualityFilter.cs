using System.Text.RegularExpressions;
using LeadSphere.Discovery.Function.Infrastructure;
using LeadSphere.Discovery.Function.Models;

namespace LeadSphere.Discovery.Function.Services;

internal static class GenericEmailFilter
{
    private static readonly HashSet<string> GenericLocalParts = new(StringComparer.OrdinalIgnoreCase)
    {
        "hello", "hi", "info", "contact", "contacts", "support", "help", "sales", "admin",
        "office", "team", "marketing", "hr", "careers", "jobs", "press", "media", "webmaster",
        "noreply", "no-reply", "donotreply", "do-not-reply", "mail", "email", "enquiries",
        "inquiry", "inquiries", "service", "customerservice", "billing", "accounts", "general"
    };

    public static bool IsGeneric(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            return true;

        var local = email.Split('@')[0].Trim().ToLowerInvariant();
        if (GenericLocalParts.Contains(local))
            return true;

        return local is "contacto" or "ventas" or "soporte" or "informacion";
    }

    public static IEnumerable<string> FilterPersonalEmails(IEnumerable<string> emails, string? companyDomain)
    {
        var normalizedDomain = companyDomain?.Trim().ToLowerInvariant();

        foreach (var email in emails.Where(e => !string.IsNullOrWhiteSpace(e)))
        {
            var trimmed = email.Trim().ToLowerInvariant();
            if (IsGeneric(trimmed))
                continue;

            if (!string.IsNullOrWhiteSpace(normalizedDomain))
            {
                var emailDomain = trimmed.Split('@').LastOrDefault();
                if (!string.Equals(emailDomain, normalizedDomain, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            yield return trimmed;
        }
    }
}

internal static class ContactQualityFilter
{
    private static readonly string[] DecisionMakerKeywords =
    [
        "ceo", "chief", "founder", "co-founder", "president", "owner", "partner",
        "cto", "cfo", "coo", "cmo", "cro", "chro", "vp", "vice president", "director",
        "head of", "general manager", "country manager", "regional director",
        "managing director", "commercial director", "sales director", "business development",
        "gerente general", "director general", "director comercial", "socio",
        "principal", "executive", "leadership"
    ];

    public static bool HasRealName(AiContactData contact)
    {
        if (!string.IsNullOrWhiteSpace(contact.FirstName) && !string.IsNullOrWhiteSpace(contact.LastName))
            return true;

        if (string.IsNullOrWhiteSpace(contact.FullName))
            return false;

        var parts = contact.FullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts.All(p => p.Length > 1);
    }

    public static bool IsDecisionMakerTitle(string? jobTitle)
    {
        if (string.IsNullOrWhiteSpace(jobTitle))
            return false;

        var lower = jobTitle.ToLowerInvariant();
        return DecisionMakerKeywords.Any(k => lower.Contains(k, StringComparison.Ordinal));
    }

    public static bool IsQualityContact(AiContactData contact)
    {
        if (!HasRealName(contact))
            return false;

        if (!string.IsNullOrWhiteSpace(contact.Email) && GenericEmailFilter.IsGeneric(contact.Email))
            return false;

        var hasLinkedIn = LinkedInContactUrl.IsPersonalProfile(contact.LinkedInUrl);

        var hasDecisionTitle = IsDecisionMakerTitle(contact.JobTitle);
        var hasPersonalEmail = !string.IsNullOrWhiteSpace(contact.Email) && !GenericEmailFilter.IsGeneric(contact.Email);
        var hasPhone = !string.IsNullOrWhiteSpace(contact.Phone);

        return hasLinkedIn || (hasDecisionTitle && (hasPersonalEmail || hasPhone || hasLinkedIn));
    }

    public static List<AiContactData> MergeAndRank(
        IEnumerable<AiContactData> primary,
        IEnumerable<AiContactData> secondary,
        string? companyLinkedInUrl = null)
    {
        var merged = new List<AiContactData>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var contact in primary.Concat(secondary))
        {
            contact.LinkedInUrl = LinkedInContactUrl.NormalizePersonal(contact.LinkedInUrl, companyLinkedInUrl);

            if (!IsQualityContact(contact))
                continue;

            var key = !string.IsNullOrWhiteSpace(contact.LinkedInUrl)
                ? contact.LinkedInUrl.Trim().ToLowerInvariant()
                : !string.IsNullOrWhiteSpace(contact.Email)
                    ? contact.Email.Trim().ToLowerInvariant()
                    : contact.FullName?.Trim().ToLowerInvariant() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(key) || !seen.Add(key))
                continue;

            merged.Add(contact);
        }

        return merged
            .OrderByDescending(ContactReachabilityScore)
            .ThenByDescending(c => IsDecisionMakerTitle(c.JobTitle) ? 1 : 0)
            .ThenByDescending(c => !string.IsNullOrWhiteSpace(c.LinkedInUrl) ? 1 : 0)
            .ToList();
    }

    public static int ContactReachabilityScore(AiContactData contact)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(contact.Email) && !GenericEmailFilter.IsGeneric(contact.Email))
            score += 4;
        if (!string.IsNullOrWhiteSpace(contact.Phone))
            score += 3;
        if (LinkedInContactUrl.IsPersonalProfile(contact.LinkedInUrl))
            score += 1;
        return score;
    }

    public static bool HasReachableChannel(AiContactData contact) =>
        (!string.IsNullOrWhiteSpace(contact.Email) && !GenericEmailFilter.IsGeneric(contact.Email))
        || !string.IsNullOrWhiteSpace(contact.Phone);
}

internal static class SearchResultRelevanceFilter
{
    private static readonly HashSet<string> LowQualityHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "wikipedia.org", "youtube.com", "reddit.com", "quora.com", "medium.com",
        "amazon.com", "ebay.com", "pinterest.com", "tiktok.com", "github.com"
    };

    private static readonly string[] IrrelevantBusinessKeywords =
    [
        "consulting", "project management", "software development", "analytics governance",
        "training and education", "trade data", "import export database", "market intelligence",
        "web design", "digital agency", "recruitment", "staffing agency"
    ];

    public static IReadOnlyList<WebSearchResult> FilterAndRank(
        SearchRecord search,
        IEnumerable<WebSearchResult> results,
        double minScore = 0.20)
    {
        var intent = SearchIntentResolver.Resolve(search);

        return results
            .Select(r => new { Result = r, Score = Score(r, intent) })
            .Where(x => x.Score >= minScore)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Result)
            .ToList();
    }

    private static double Score(WebSearchResult result, SearchIntent intent)
    {
        var text = $"{result.Title} {result.Snippet} {result.Url}".ToLowerInvariant();
        var score = 0.10;

        foreach (var token in intent.IndustryKeywords)
        {
            if (text.Contains(token, StringComparison.Ordinal))
                score += 0.22;
        }

        if (!string.IsNullOrWhiteSpace(intent.Location))
        {
            foreach (var token in Tokenize(intent.Location))
            {
                if (token.Length > 2 && text.Contains(token, StringComparison.Ordinal))
                    score += 0.18;
            }
        }

        foreach (var keyword in intent.ProfileKeywords)
        {
            if (text.Contains(keyword, StringComparison.Ordinal))
                score += 0.10;
        }

        foreach (var keyword in intent.BusinessTypeKeywords)
        {
            if (text.Contains(keyword, StringComparison.Ordinal))
                score += 0.20;
        }

        if (text.Contains("company", StringComparison.Ordinal) ||
            text.Contains("empresa", StringComparison.Ordinal) ||
            text.Contains("services", StringComparison.Ordinal) ||
            text.Contains("servicios", StringComparison.Ordinal))
            score += 0.05;

        var domain = DomainNormalizer.ExtractDomain(result.Url);
        if (domain is not null && LowQualityHosts.Contains(domain))
            score -= 1.0;

        if (DomainNormalizer.IsBlockedUrl(result.Url) || DomainNormalizer.IsDirectoryOrSocialUrl(result.Url))
            score -= 2.0;

        if (text.Contains("jobs", StringComparison.Ordinal) || text.Contains("careers", StringComparison.Ordinal))
            score -= 0.25;

        if (intent.BusinessTypeKeywords.Count > 0 &&
            IrrelevantBusinessKeywords.Any(k => text.Contains(k, StringComparison.Ordinal)) &&
            !intent.BusinessTypeKeywords.Any(k => text.Contains(k, StringComparison.Ordinal)))
            score -= 0.55;

        return Math.Clamp(score, 0, 2.0);
    }

    private static IEnumerable<string> Tokenize(string value) =>
        value.ToLowerInvariant()
            .Split([' ', ',', '-', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 2);
}
