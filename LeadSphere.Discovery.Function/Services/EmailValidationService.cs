using System.Text.RegularExpressions;
using LeadSphere.Discovery.Function.Models;

namespace LeadSphere.Discovery.Function.Services;

public interface IEmailValidationService
{
    Task<EmailValidationResult> ValidateAsync(string email, CancellationToken cancellationToken);
    Task<IReadOnlyList<EmailValidationResult>> ValidateManyAsync(IEnumerable<string> emails, CancellationToken cancellationToken);
}

public sealed class EmailValidationService : IEmailValidationService
{
    private static readonly Regex EmailFormatRegex = new(
        @"^[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> DisposableDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "mailinator.com", "guerrillamail.com", "tempmail.com", "10minutemail.com",
        "yopmail.com", "throwaway.email", "getnada.com", "sharklasers.com"
    };

    public async Task<EmailValidationResult> ValidateAsync(string email, CancellationToken cancellationToken)
    {
        email = email.Trim().ToLowerInvariant();
        var result = new EmailValidationResult { Email = email };

        if (!EmailFormatRegex.IsMatch(email))
        {
            result.Status = "invalid";
            return result;
        }

        result.IsValidFormat = true;
        var domain = email.Split('@')[1];

        if (DisposableDomains.Contains(domain))
        {
            result.Status = "disposable";
            return result;
        }

        result.HasMxRecord = await HasMxRecordAsync(domain, cancellationToken);
        result.Status = result.HasMxRecord ? "valid" : "risky";
        return result;
    }

    public async Task<IReadOnlyList<EmailValidationResult>> ValidateManyAsync(IEnumerable<string> emails, CancellationToken cancellationToken)
    {
        var unique = emails
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var results = new List<EmailValidationResult>();
        foreach (var email in unique)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await ValidateAsync(email, cancellationToken));
        }

        return results;
    }

    private static async Task<bool> HasMxRecordAsync(string domain, CancellationToken cancellationToken)
    {
        try
        {
            var lookup = new DnsClient.LookupClient();
            var mx = await lookup.QueryAsync(domain, DnsClient.QueryType.MX, cancellationToken: cancellationToken);
            return mx.Answers.MxRecords().Any();
        }
        catch
        {
            return false;
        }
    }
}
