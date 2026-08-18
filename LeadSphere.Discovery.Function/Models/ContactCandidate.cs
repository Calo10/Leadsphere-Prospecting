namespace LeadSphere.Discovery.Function.Models;

public sealed class ContactCandidate
{
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? JobTitle { get; set; }
    public string? LinkedInUrl { get; set; }
    public double? FitScore { get; set; }
    public double? ConfidenceScore { get; set; }
    public string? AiSummary { get; set; }

    public string FullName =>
        string.Join(' ', new[] { FirstName, LastName }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
}
