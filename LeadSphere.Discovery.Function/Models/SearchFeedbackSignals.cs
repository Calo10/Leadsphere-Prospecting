using System.Text;
using System.Text.Json;

namespace LeadSphere.Discovery.Function.Models;

public sealed class SearchFeedbackExample
{
    public string Kind { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Industry { get; set; }
    public string? Location { get; set; }
    public string? Domain { get; set; }
    public int? EmployeeCount { get; set; }
    public string? JobTitle { get; set; }
    public string? CompanyName { get; set; }
    public string? AiSummary { get; set; }
    public string? Email { get; set; }
    public string? LinkedInUrl { get; set; }
}

public sealed class SearchFeedbackSignals
{
    public string? Notes { get; set; }
    public List<SearchFeedbackExample> ThumbsUp { get; set; } = [];
    public List<SearchFeedbackExample> ThumbsDown { get; set; } = [];

    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);
    public bool HasRatings => ThumbsUp.Count > 0 || ThumbsDown.Count > 0;
    public bool HasAny => HasNotes || HasRatings;

    public static string OriginalProfile(string? profileDescription)
    {
        var profile = (profileDescription ?? string.Empty).Trim();
        var idx = profile.IndexOf("\n---", StringComparison.Ordinal);
        return idx >= 0 ? profile[..idx].Trim() : profile;
    }

    public static SearchFeedbackSignals? Resolve(SearchRecord search)
    {
        SearchFeedbackSignals? signals = null;
        if (!string.IsNullOrWhiteSpace(search.FeedbackSignalsJson))
        {
            try
            {
                signals = JsonSerializer.Deserialize<SearchFeedbackSignals>(search.FeedbackSignalsJson, JsonDefaults.Web);
            }
            catch
            {
                signals = null;
            }
        }

        signals ??= new SearchFeedbackSignals();
        if (string.IsNullOrWhiteSpace(signals.Notes))
            signals.Notes = string.IsNullOrWhiteSpace(search.Feedback) ? null : search.Feedback.Trim();

        if (!signals.HasRatings || !signals.HasNotes)
            MergeLegacyFromProfile(search.ProfileDescription, signals);

        return signals.HasAny ? signals : null;
    }

    public string ToPromptBlock()
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("=== HUMAN FEEDBACK (strongest signal — overrides a loose original match) ===");
        if (HasNotes)
        {
            sb.AppendLine("WRITTEN FEEDBACK (must follow):");
            sb.AppendLine(Notes!.Trim());
        }

        if (ThumbsUp.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("THUMBS UP — confirmed good-fit examples. Prefer the same industry, size, location, seniority, and role pattern:");
            foreach (var example in ThumbsUp.Take(20))
                sb.AppendLine($"- {FormatExample(example)}");
        }

        if (ThumbsDown.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("THUMBS DOWN — confirmed bad-fit examples. Score below 0.4 if the candidate is the same type of miss:");
            foreach (var example in ThumbsDown.Take(20))
                sb.AppendLine($"- {FormatExample(example)}");
        }

        sb.AppendLine("If written feedback and thumbs disagree, follow the written feedback and still reject exact thumbs-down companies/people.");
        return sb.ToString();
    }

    public bool RejectsCompany(string? domain, string? name)
    {
        foreach (var example in ThumbsDown.Where(e => IsCompany(e)))
        {
            if (SameDomain(example.Domain, domain))
                return true;
            if (SameText(example.Name, name))
                return true;
        }

        return false;
    }

    public bool RejectsContact(string? email, string? linkedInUrl, string? name)
    {
        foreach (var example in ThumbsDown.Where(e => IsContact(e)))
        {
            if (SameText(example.Email, email))
                return true;
            if (SameLinkedIn(example.LinkedInUrl, linkedInUrl))
                return true;
            if (SameText(example.Name, name) && !string.IsNullOrWhiteSpace(name))
                return true;
        }

        return false;
    }

    private static void MergeLegacyFromProfile(string? profileDescription, SearchFeedbackSignals signals)
    {
        var profile = profileDescription ?? string.Empty;
        if (string.IsNullOrWhiteSpace(signals.Notes))
        {
            var notesStart = profile.IndexOf("USER FEEDBACK", StringComparison.OrdinalIgnoreCase);
            if (notesStart >= 0)
            {
                var after = profile[(notesStart + "USER FEEDBACK".Length)..];
                var colon = after.IndexOf('\n');
                if (colon >= 0)
                    after = after[(colon + 1)..];
                var end = after.IndexOf("\n\nHUMAN RATINGS", StringComparison.OrdinalIgnoreCase);
                if (end < 0)
                    end = after.IndexOf("\nTHUMBS ", StringComparison.OrdinalIgnoreCase);
                var notes = (end >= 0 ? after[..end] : after).Replace("(follow this over a loose original match):", string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(notes))
                    signals.Notes = notes;
            }
        }
    }

    private static string FormatExample(SearchFeedbackExample example)
    {
        var kind = string.IsNullOrWhiteSpace(example.Kind) ? "Example" : example.Kind.Trim();
        var parts = new[]
        {
            kind,
            example.Name,
            example.JobTitle,
            example.CompanyName,
            example.Industry,
            example.Location,
            example.Domain,
            example.EmployeeCount is int n ? $"{n} employees" : null,
            example.AiSummary
        };
        return string.Join(" | ", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));
    }

    private static bool IsCompany(SearchFeedbackExample example) =>
        string.Equals(example.Kind, "company", StringComparison.OrdinalIgnoreCase);

    private static bool IsContact(SearchFeedbackExample example) =>
        string.Equals(example.Kind, "contact", StringComparison.OrdinalIgnoreCase);

    private static bool SameText(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool SameDomain(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;
        var a = left.Trim().ToLowerInvariant().TrimStart().Replace("www.", string.Empty);
        var b = right.Trim().ToLowerInvariant().Replace("www.", string.Empty);
        return a == b;
    }

    private static bool SameLinkedIn(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;
        static string Norm(string value) => value.Trim().TrimEnd('/').ToLowerInvariant();
        return Norm(left) == Norm(right);
    }
}
