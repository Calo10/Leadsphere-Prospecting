namespace LeadSphere.Discovery.Function.Constants;

public static class SignalStatuses
{
    public const string Active = "active";
    public const string Collecting = "collecting";
    public const string Expired = "expired";
    public const string Silenced = "silenced";
    public const string Cancelled = "cancelled";
}

public static class SignalEventTypes
{
    public const string EmployeeCountChanged = "EmployeeCountChanged";
    public const string DescriptionChanged = "DescriptionChanged";
    public const string NewContactsDiscovered = "NewContactsDiscovered";
    public const string IndustryChanged = "IndustryChanged";
    public const string LocationChanged = "LocationChanged";
    public const string WebsiteChanged = "WebsiteChanged";
    public const string CompanyNameChanged = "CompanyNameChanged";
    public const string NewsDetected = "NewsDetected";
    public const string SocialActivityDetected = "SocialActivityDetected";
    public const string TechnologyChanged = "TechnologyChanged";
    public const string NewExecutivesDiscovered = "NewExecutivesDiscovered";
    public const string SnapshotCreated = "SnapshotCreated";
    public const string SignalExpired = "SignalExpired";
}

public static class SignalSeverities
{
    public const string Info = "info";
    public const string Medium = "medium";
    public const string High = "high";
}
