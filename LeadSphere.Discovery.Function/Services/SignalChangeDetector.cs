using System.Text.RegularExpressions;
using LeadSphere.Discovery.Function.Constants;
using LeadSphere.Discovery.Function.Models;

namespace LeadSphere.Discovery.Function.Services;

public static class SignalChangeDetector
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public static IReadOnlyList<SignalEventDraft> Detect(SignalSnapshotPayload? previous, SignalSnapshotPayload current)
    {
        if (previous is null)
            return Array.Empty<SignalEventDraft>();

        var events = new List<SignalEventDraft>();

        if (previous.EmployeeCount != current.EmployeeCount)
        {
            events.Add(Change(
                SignalEventTypes.EmployeeCountChanged,
                SignalSeverities.Medium,
                "Employee Count Updated",
                $"Employee count changed from {FormatCount(previous.EmployeeCount)} to {FormatCount(current.EmployeeCount)}.",
                FormatCount(previous.EmployeeCount),
                FormatCount(current.EmployeeCount)));
        }

        if (!string.Equals(NormalizeText(previous.Description), NormalizeText(current.Description), StringComparison.Ordinal))
        {
            events.Add(Change(
                SignalEventTypes.DescriptionChanged,
                SignalSeverities.Info,
                "Company Description Updated",
                "The company description changed since the last snapshot.",
                Truncate(previous.Description),
                Truncate(current.Description)));
        }

        if (current.ContactCount > previous.ContactCount)
        {
            var added = current.ContactCount - previous.ContactCount;
            events.Add(Change(
                SignalEventTypes.NewContactsDiscovered,
                SignalSeverities.Medium,
                "New Contacts Discovered",
                $"{added} new contact{(added == 1 ? "" : "s")} discovered ({previous.ContactCount} → {current.ContactCount}).",
                previous.ContactCount.ToString(),
                current.ContactCount.ToString()));
        }

        if (current.NewsCount > previous.NewsCount)
        {
            var added = current.NewsCount - previous.NewsCount;
            events.Add(Change(
                SignalEventTypes.NewsDetected,
                SignalSeverities.Info,
                "New Company News",
                $"{added} new news item{(added == 1 ? "" : "s")} detected.",
                previous.NewsCount.ToString(),
                current.NewsCount.ToString()));
        }

        if (!EqualsNormalized(previous.Industry, current.Industry))
        {
            events.Add(Change(
                SignalEventTypes.IndustryChanged,
                SignalSeverities.Medium,
                "Industry Updated",
                $"Industry changed from {Display(previous.Industry)} to {Display(current.Industry)}.",
                previous.Industry,
                current.Industry));
        }

        if (!EqualsNormalized(previous.Location, current.Location))
        {
            events.Add(Change(
                SignalEventTypes.LocationChanged,
                SignalSeverities.Medium,
                "Location Updated",
                $"Location changed from {Display(previous.Location)} to {Display(current.Location)}.",
                previous.Location,
                current.Location));
        }

        if (!EqualsNormalized(previous.Website, current.Website))
        {
            events.Add(Change(
                SignalEventTypes.WebsiteChanged,
                SignalSeverities.Medium,
                "Website Updated",
                $"Website changed from {Display(previous.Website)} to {Display(current.Website)}.",
                previous.Website,
                current.Website));
        }

        if (!EqualsNormalized(previous.CompanyName, current.CompanyName))
        {
            events.Add(Change(
                SignalEventTypes.CompanyNameChanged,
                SignalSeverities.Medium,
                "Company Name Updated",
                $"Company name changed from {Display(previous.CompanyName)} to {Display(current.CompanyName)}.",
                previous.CompanyName,
                current.CompanyName));
        }

        var previousSocial = NormalizeSet(previous.SocialLinks);
        var currentSocial = NormalizeSet(current.SocialLinks);
        if (!previousSocial.SetEquals(currentSocial))
        {
            events.Add(Change(
                SignalEventTypes.SocialActivityDetected,
                SignalSeverities.Info,
                "Social Links Updated",
                "Company social links changed since the last snapshot.",
                string.Join(", ", previousSocial.OrderBy(x => x)),
                string.Join(", ", currentSocial.OrderBy(x => x))));
        }

        var previousTech = NormalizeSet(previous.Technologies);
        var currentTech = NormalizeSet(current.Technologies);
        if (!previousTech.SetEquals(currentTech) && (previousTech.Count > 0 || currentTech.Count > 0))
        {
            events.Add(Change(
                SignalEventTypes.TechnologyChanged,
                SignalSeverities.Info,
                "Technologies Updated",
                "Detected technologies changed since the last snapshot.",
                string.Join(", ", previousTech.OrderBy(x => x)),
                string.Join(", ", currentTech.OrderBy(x => x))));
        }

        var previousNames = NormalizeSet(previous.Contacts.Select(c => c.FullName));
        var currentNames = NormalizeSet(current.Contacts.Select(c => c.FullName));
        var newExecs = currentNames.Except(previousNames).OrderBy(x => x).ToList();
        if (newExecs.Count > 0)
        {
            events.Add(Change(
                SignalEventTypes.NewExecutivesDiscovered,
                SignalSeverities.Medium,
                "New Executives Discovered",
                $"New people found: {string.Join(", ", newExecs)}.",
                string.Join(", ", previousNames.OrderBy(x => x)),
                string.Join(", ", currentNames.OrderBy(x => x))));
        }

        return events;
    }

    private static SignalEventDraft Change(
        string type,
        string severity,
        string title,
        string description,
        string? previous,
        string? next) =>
        new()
        {
            EventType = type,
            Severity = severity,
            Title = title,
            Description = description,
            PreviousValue = Truncate(previous),
            NewValue = Truncate(next)
        };

    public static string NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return Whitespace.Replace(value.Trim(), " ").ToLowerInvariant();
    }

    private static bool EqualsNormalized(string? left, string? right) =>
        string.Equals(NormalizeText(left), NormalizeText(right), StringComparison.Ordinal);

    private static HashSet<string> NormalizeSet(IEnumerable<string?> values) =>
        values
            .Select(NormalizeText)
            .Where(v => v.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

    private static string FormatCount(int? value) => value?.ToString() ?? "—";

    private static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();

    private static string? Truncate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        return value.Length <= 500 ? value : value[..497] + "...";
    }
}
