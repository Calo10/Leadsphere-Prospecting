namespace LeadSphere.Discovery.Function.Models;

public enum LocationGranularity
{
    None = 0,
    Neighborhood = 1,
    City = 2,
    Metro = 3,
    State = 4,
    Country = 5
}

public sealed class ResolvedSearchLocation
{
    public string Raw { get; init; } = string.Empty;
    public string QueryLabel { get; init; } = string.Empty;
    public string? SerpApiLocation { get; init; }
    public string? City { get; init; }
    public string? Region { get; init; }
    public string? RegionCode { get; init; }
    public string? Country { get; init; }
    public string CountryCode { get; init; } = "us";
    public LocationGranularity Granularity { get; init; } = LocationGranularity.None;
    public IReadOnlyList<string> MatchTokens { get; init; } = [];
    public IReadOnlyList<string> ZipCodes { get; init; } = [];
    public IReadOnlyList<string> ChildTokens { get; init; } = [];
    public IReadOnlyList<string> ParentTokens { get; init; } = [];

    public bool HasConstraint => Granularity != LocationGranularity.None && MatchTokens.Count > 0;
}
