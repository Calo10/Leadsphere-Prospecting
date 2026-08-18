using LeadSphere.Discovery.Function.Infrastructure;
using LeadSphere.Discovery.Function.Models;

namespace LeadSphere.Discovery.Function.Repositories;

internal static class PersistableContactMapper
{
    public static PersistableContact? Map(AiContactData contact, string? locationHint)
    {
        var firstName = ValueNormalizer.Text(contact.FirstName);
        var lastName = ValueNormalizer.Text(contact.LastName);
        var fullName = ValueNormalizer.Text(contact.FullName);

        if (firstName is null && lastName is null && fullName is not null)
        {
            var parts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 1)
                firstName = parts[0];
            if (parts.Length >= 2)
                lastName = string.Join(' ', parts.Skip(1));
        }

        var email = ValueNormalizer.Text(contact.Email)?.ToLowerInvariant();
        var phone = PhoneNormalizer.Normalize(contact.Phone, locationHint);
        var jobTitle = ValueNormalizer.Text(contact.JobTitle);
        var linkedInUrl = LinkedInContactUrl.NormalizePersonal(contact.LinkedInUrl);

        if (firstName is null && lastName is null)
            return null;

        if (email is null && phone is null && linkedInUrl is null)
            return null;

        return new PersistableContact(firstName, lastName, email, phone, jobTitle, linkedInUrl);
    }
}

internal sealed record PersistableContact(
    string? FirstName,
    string? LastName,
    string? Email,
    string? Phone,
    string? JobTitle,
    string? LinkedInUrl);

internal static class PersistableCompanyMapper
{
    public static PersistableCompany Map(
        AiCompanyData company,
        AiExtractionResult extraction,
        CompanyEnrichmentData enrichment)
    {
        return new PersistableCompany(
            Name: ValueNormalizer.Text(company.Name) ?? company.Name.Trim(),
            Domain: ValueNormalizer.Text(company.Domain)?.ToLowerInvariant(),
            Website: ValueNormalizer.Url(company.Website),
            Industry: ValueNormalizer.Text(company.Industry),
            EmployeeCount: company.EmployeeCount is > 0 ? company.EmployeeCount : null,
            Location: ValueNormalizer.Text(company.Location),
            Description: ValueNormalizer.Text(company.Description),
            LogoUrl: ValueNormalizer.Url(enrichment.LogoUrl),
            LinkedInUrl: ValueNormalizer.Url(enrichment.LinkedInUrl),
            TwitterUrl: ValueNormalizer.Url(enrichment.TwitterUrl),
            FacebookUrl: ValueNormalizer.Url(enrichment.FacebookUrl),
            InstagramUrl: ValueNormalizer.Url(enrichment.InstagramUrl),
            CrunchbaseUrl: ValueNormalizer.Url(enrichment.CrunchbaseUrl),
            MetadataJson: MetadataBuilder.ForCompany(extraction, enrichment));
    }
}

internal sealed record PersistableCompany(
    string Name,
    string? Domain,
    string? Website,
    string? Industry,
    int? EmployeeCount,
    string? Location,
    string? Description,
    string? LogoUrl,
    string? LinkedInUrl,
    string? TwitterUrl,
    string? FacebookUrl,
    string? InstagramUrl,
    string? CrunchbaseUrl,
    string? MetadataJson);
