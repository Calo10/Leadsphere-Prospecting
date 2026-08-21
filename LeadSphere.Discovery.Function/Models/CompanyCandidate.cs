namespace LeadSphere.Discovery.Function.Models;

public sealed class CompanyCandidate
{
    public string Name { get; set; } = string.Empty;
    public string? Website { get; set; }
    public string? Domain { get; set; }
    public string? Industry { get; set; }
    public string? Location { get; set; }
    public string? Description { get; set; }
    public int? EmployeeCount { get; set; }
    public List<string> Emails { get; set; } = [];
    public List<string> Phones { get; set; } = [];
    public List<string> PossiblePeopleNames { get; set; } = [];
    public List<string> JobTitles { get; set; } = [];
    public string? SourceUrl { get; set; }
    public string? RawText { get; set; }
    public string? HomepageHtml { get; set; }
    public List<string> LogoCandidateUrls { get; set; } = [];
    public Dictionary<string, string> SocialLinks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<AiContactData> WebsiteLinkedInContacts { get; set; } = [];
    public List<AiContactData> LinkedInContacts { get; set; } = [];
}

public sealed class WebSearchResult
{
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? Snippet { get; set; }
    public string? Domain { get; set; }
}

public sealed class AiExtractionResult
{
    public AiCompanyData? Company { get; set; }
    public List<AiContactData> Contacts { get; set; } = [];
    public double? FitScore { get; set; }
    public double? ConfidenceScore { get; set; }
    public string? AiSummary { get; set; }
}

public sealed class AiCompanyData
{
    public string Name { get; set; } = string.Empty;
    public string? Website { get; set; }
    public string? Domain { get; set; }
    public string? Industry { get; set; }
    public string? Location { get; set; }
    public string? Description { get; set; }
    public int? EmployeeCount { get; set; }
}

public sealed class AiContactData
{
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? FullName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? JobTitle { get; set; }
    public string? LinkedInUrl { get; set; }
}
