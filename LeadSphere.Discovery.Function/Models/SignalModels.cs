namespace LeadSphere.Discovery.Function.Models;

public sealed class SignalDueJob
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid CompanyId { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset EndDate { get; set; }
}

public sealed class SignalSnapshotRecord
{
    public Guid Id { get; set; }
    public Guid SignalId { get; set; }
    public DateTimeOffset SnapshotDate { get; set; }
    public string? CompanyName { get; set; }
    public int? EmployeeCount { get; set; }
    public int? ContactCount { get; set; }
    public int? NewsCount { get; set; }
    public string? Industry { get; set; }
    public string? Description { get; set; }
    public string? Website { get; set; }
    public string? Location { get; set; }
    public string? RawJson { get; set; }
}

public sealed class SignalSnapshotPayload
{
    public string? CompanyName { get; set; }
    public string? Description { get; set; }
    public int? EmployeeCount { get; set; }
    public string? Industry { get; set; }
    public string? Website { get; set; }
    public string? Location { get; set; }
    public int ContactCount { get; set; }
    public int NewsCount { get; set; }
    public IReadOnlyList<SignalSnapshotNewsItem> NewsItems { get; set; } = Array.Empty<SignalSnapshotNewsItem>();
    public IReadOnlyList<string> SocialLinks { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Technologies { get; set; } = Array.Empty<string>();
    public IReadOnlyList<SignalSnapshotContact> Contacts { get; set; } = Array.Empty<SignalSnapshotContact>();
}

public sealed class SignalSnapshotNewsItem
{
    public string Title { get; set; } = string.Empty;
    public string? Url { get; set; }
    public string? Source { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public string? ImageUrl { get; set; }
    public string? Snippet { get; set; }
    public string? Kind { get; set; }
}

public sealed class SignalSnapshotContact
{
    public string? FullName { get; set; }
    public string? JobTitle { get; set; }
    public string? LinkedInUrl { get; set; }
}

public sealed class SignalEventDraft
{
    public string EventType { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? PreviousValue { get; set; }
    public string? NewValue { get; set; }
}

internal sealed class CompanySnapshotRow
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public int? EmployeeCount { get; set; }
    public string? Industry { get; set; }
    public string? Website { get; set; }
    public string? Domain { get; set; }
    public string? Location { get; set; }
    public string? LinkedInUrl { get; set; }
    public string? TwitterUrl { get; set; }
    public string? FacebookUrl { get; set; }
    public string? InstagramUrl { get; set; }
    public string? CrunchbaseUrl { get; set; }
    public string? MetadataJson { get; set; }
}

internal sealed class ContactSnapshotRow
{
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? JobTitle { get; set; }
    public string? LinkedInUrl { get; set; }
}
