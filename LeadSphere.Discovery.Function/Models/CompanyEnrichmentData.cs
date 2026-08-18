namespace LeadSphere.Discovery.Function.Models;

public sealed class CompanyEnrichmentData
{
    public string? LogoUrl { get; set; }
    public string? LinkedInUrl { get; set; }
    public string? TwitterUrl { get; set; }
    public string? FacebookUrl { get; set; }
    public string? InstagramUrl { get; set; }
    public string? CrunchbaseUrl { get; set; }
    public List<EmailValidationResult> EmailValidations { get; set; } = [];
    public List<string> EnrichmentSources { get; set; } = [];
}

public sealed class EmailValidationResult
{
    public string Email { get; set; } = string.Empty;
    public bool IsValidFormat { get; set; }
    public bool HasMxRecord { get; set; }
    public string Status { get; set; } = "unknown";
}
