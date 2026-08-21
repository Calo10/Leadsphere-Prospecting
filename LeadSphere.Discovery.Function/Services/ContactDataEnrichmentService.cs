using System.Text;
using System.Text.RegularExpressions;
using LeadSphere.Discovery.Function.Infrastructure;
using LeadSphere.Discovery.Function.Models;
using LeadSphere.Discovery.Function.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeadSphere.Discovery.Function.Services;

public interface IContactDataEnrichmentService
{
    Task EnrichAsync(
        IList<AiContactData> contacts,
        CompanyCandidate candidate,
        string companyName,
        string? locationHint,
        CancellationToken cancellationToken);
}

public sealed class ContactDataEnrichmentService : IContactDataEnrichmentService
{
    private static readonly Regex EmailInTextRegex = new(
        @"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PhoneInTextRegex = new(
        @"(?:(?:tel|phone|móvil|celular|call|llamar)[:\s]*)?\+?\d[\d\s().\-]{7,}\d",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IWebSearchService _webSearch;
    private readonly IEmailValidationService _emailValidation;
    private readonly DiscoveryOptions _options;
    private readonly ILogger<ContactDataEnrichmentService> _logger;

    public ContactDataEnrichmentService(
        IWebSearchService webSearch,
        IEmailValidationService emailValidation,
        IOptions<DiscoveryOptions> options,
        ILogger<ContactDataEnrichmentService> logger)
    {
        _webSearch = webSearch;
        _emailValidation = emailValidation;
        _options = options.Value;
        _logger = logger;
    }

    public async Task EnrichAsync(
        IList<AiContactData> contacts,
        CompanyCandidate candidate,
        string companyName,
        string? locationHint,
        CancellationToken cancellationToken)
    {
        var domain = candidate.Domain?.Trim().ToLowerInvariant();
        var companyPhones = candidate.Phones
            .Select(p => PhoneNormalizer.Normalize(p, locationHint))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct()
            .ToList();

        var usedEmails = new HashSet<string>(
            contacts.Where(c => !string.IsNullOrWhiteSpace(c.Email)).Select(c => c.Email!.Trim().ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);

        foreach (var contact in contacts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            contact.Email = ValueNormalizer.Text(contact.Email)?.ToLowerInvariant();
            contact.Phone = PhoneNormalizer.Normalize(contact.Phone, locationHint);

            if (!string.IsNullOrWhiteSpace(contact.Email) && GenericEmailFilter.IsGeneric(contact.Email))
                contact.Email = null;

            if (string.IsNullOrWhiteSpace(contact.Email))
            {
                var matched = MatchEmailFromScraped(contact, candidate.Emails, usedEmails);
                if (matched is not null)
                {
                    contact.Email = matched;
                    usedEmails.Add(matched);
                }
            }

            if (string.IsNullOrWhiteSpace(contact.Email) && !string.IsNullOrWhiteSpace(domain))
            {
                var guessed = await GuessEmailAsync(contact, domain, usedEmails, cancellationToken);
                if (guessed is not null)
                {
                    contact.Email = guessed;
                    usedEmails.Add(guessed);
                }
            }

            if (string.IsNullOrWhiteSpace(contact.Email) && _options.EnableContactWebEnrichment)
            {
                var found = await FindEmailViaWebAsync(contact, companyName, domain, usedEmails, cancellationToken);
                if (found is not null)
                {
                    contact.Email = found;
                    usedEmails.Add(found);
                }
            }

            if (string.IsNullOrWhiteSpace(contact.Phone) && _options.EnableContactWebEnrichment)
            {
                var phone = await FindPhoneViaWebAsync(contact, companyName, domain, locationHint, cancellationToken);
                if (phone is not null)
                    contact.Phone = phone;
            }

            if (string.IsNullOrWhiteSpace(contact.Phone) && companyPhones.Count > 0)
                contact.Phone = companyPhones[0];
        }

        var withEmail = contacts.Count(c => !string.IsNullOrWhiteSpace(c.Email));
        var withPhone = contacts.Count(c => !string.IsNullOrWhiteSpace(c.Phone));
        _logger.LogInformation(
            "Contact data enrichment for {Company}: {WithEmail}/{Total} emails, {WithPhone}/{Total} phones",
            companyName,
            withEmail,
            contacts.Count,
            withPhone,
            contacts.Count);
    }

    private static string? MatchEmailFromScraped(
        AiContactData contact,
        IEnumerable<string> scrapedEmails,
        HashSet<string> usedEmails)
    {
        var (first, last) = GetNameParts(contact);
        if (first is null || last is null)
            return null;

        var firstNorm = NormalizeToken(first);
        var lastNorm = NormalizeToken(last);
        if (firstNorm.Length < 2 || lastNorm.Length < 2)
            return null;

        string? best = null;
        var bestScore = 0;

        foreach (var email in scrapedEmails)
        {
            if (usedEmails.Contains(email) || GenericEmailFilter.IsGeneric(email))
                continue;

            var local = email.Split('@')[0].ToLowerInvariant();
            var score = 0;

            if (local == $"{firstNorm}.{lastNorm}" || local == $"{firstNorm}_{lastNorm}" || local == $"{firstNorm}{lastNorm}")
                score = 5;
            else if (local == $"{firstNorm[0]}{lastNorm}" || local == $"{firstNorm}.{lastNorm[0]}")
                score = 4;
            else if (local.Contains(firstNorm, StringComparison.Ordinal) && local.Contains(lastNorm, StringComparison.Ordinal))
                score = 3;
            else if (local.StartsWith(firstNorm, StringComparison.Ordinal) || local.EndsWith(lastNorm, StringComparison.Ordinal))
                score = 2;

            if (score > bestScore)
            {
                bestScore = score;
                best = email;
            }
        }

        return bestScore >= 2 ? best : null;
    }

    private async Task<string?> GuessEmailAsync(
        AiContactData contact,
        string domain,
        HashSet<string> usedEmails,
        CancellationToken cancellationToken)
    {
        var (first, last) = GetNameParts(contact);
        if (first is null || last is null)
            return null;

        var firstNorm = NormalizeToken(first);
        var lastNorm = NormalizeToken(last);
        if (firstNorm.Length < 2 || lastNorm.Length < 2)
            return null;

        var candidates = new[]
        {
            $"{firstNorm}.{lastNorm}@{domain}",
            $"{firstNorm}{lastNorm}@{domain}",
            $"{firstNorm}_{lastNorm}@{domain}",
            $"{firstNorm[0]}{lastNorm}@{domain}",
            $"{firstNorm}@{domain}",
            $"{firstNorm}.{lastNorm[0]}@{domain}",
            $"{lastNorm}.{firstNorm}@{domain}"
        };

        foreach (var email in candidates)
        {
            if (usedEmails.Contains(email))
                continue;

            var validation = await _emailValidation.ValidateAsync(email, cancellationToken);
            if (validation.Status is "valid" or "risky")
                return email;
        }

        return null;
    }

    private async Task<string?> FindEmailViaWebAsync(
        AiContactData contact,
        string companyName,
        string? domain,
        HashSet<string> usedEmails,
        CancellationToken cancellationToken)
    {
        var fullName = GetFullName(contact);
        if (string.IsNullOrWhiteSpace(fullName))
            return null;

        var queries = new List<string>
        {
            $"\"{fullName}\" \"{companyName}\" email OR correo OR @",
        };

        if (!string.IsNullOrWhiteSpace(domain))
            queries.Add($"\"{fullName}\" \"@{domain}\"");

        queries.Add($"\"{fullName}\" contact email \"{companyName}\"");

        foreach (var query in queries.Take(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var results = await _webSearch.SearchAsync(query, maxResults: 5, context: null, cancellationToken);
                foreach (var result in results)
                {
                    var haystack = $"{result.Title} {result.Snippet} {result.Url}";
                    foreach (Match match in EmailInTextRegex.Matches(haystack))
                    {
                        var email = match.Value.Trim().ToLowerInvariant();
                        if (usedEmails.Contains(email) || GenericEmailFilter.IsGeneric(email))
                            continue;

                        if (!string.IsNullOrWhiteSpace(domain)
                            && !email.EndsWith($"@{domain}", StringComparison.OrdinalIgnoreCase)
                            && !EmailLooksPersonalForName(email, contact))
                            continue;

                        if (!EmailLooksPersonalForName(email, contact) &&
                            (string.IsNullOrWhiteSpace(domain) || !email.EndsWith($"@{domain}", StringComparison.OrdinalIgnoreCase)))
                            continue;

                        var validation = await _emailValidation.ValidateAsync(email, cancellationToken);
                        if (validation.Status is "invalid" or "disposable")
                            continue;

                        return email;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Email web search failed for {Name}", fullName);
            }
        }

        return null;
    }

    private async Task<string?> FindPhoneViaWebAsync(
        AiContactData contact,
        string companyName,
        string? domain,
        string? locationHint,
        CancellationToken cancellationToken)
    {
        var fullName = GetFullName(contact);
        if (string.IsNullOrWhiteSpace(fullName))
            return null;

        var queries = new List<string>
        {
            $"\"{fullName}\" \"{companyName}\" (phone OR teléfono OR celular OR mobile OR tel)",
        };

        if (!string.IsNullOrWhiteSpace(domain))
            queries.Add($"\"{fullName}\" {domain} (phone OR teléfono OR contact)");

        foreach (var query in queries.Take(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var results = await _webSearch.SearchAsync(query, maxResults: 5, context: null, cancellationToken);
                foreach (var result in results)
                {
                    var haystack = $"{result.Title} {result.Snippet}";
                    foreach (Match match in PhoneInTextRegex.Matches(haystack))
                    {
                        var normalized = PhoneNormalizer.Normalize(match.Value, locationHint);
                        if (normalized is not null)
                            return normalized;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Phone web search failed for {Name}", fullName);
            }
        }

        return null;
    }

    private static bool EmailLooksPersonalForName(string email, AiContactData contact)
    {
        var (first, last) = GetNameParts(contact);
        if (first is null)
            return false;

        var local = email.Split('@')[0].ToLowerInvariant();
        var firstNorm = NormalizeToken(first);
        var lastNorm = last is null ? null : NormalizeToken(last);

        if (local.Contains(firstNorm, StringComparison.Ordinal))
            return true;

        return lastNorm is not null && local.Contains(lastNorm, StringComparison.Ordinal);
    }

    private static (string? First, string? Last) GetNameParts(AiContactData contact)
    {
        if (!string.IsNullOrWhiteSpace(contact.FirstName) && !string.IsNullOrWhiteSpace(contact.LastName))
            return (contact.FirstName.Trim(), contact.LastName.Trim());

        if (string.IsNullOrWhiteSpace(contact.FullName))
            return (null, null);

        var parts = contact.FullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return (parts.FirstOrDefault(), null);

        return (parts[0], string.Join(' ', parts.Skip(1)));
    }

    private static string GetFullName(AiContactData contact)
    {
        if (!string.IsNullOrWhiteSpace(contact.FullName))
            return contact.FullName.Trim();

        return string.Join(' ', new[] { contact.FirstName, contact.LastName }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
    }

    private static string NormalizeToken(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized.Normalize(NormalizationForm.FormD))
        {
            var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == System.Globalization.UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(ch))
                sb.Append(ch);
        }

        return sb.ToString();
    }
}
