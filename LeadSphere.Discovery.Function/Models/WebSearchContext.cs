namespace LeadSphere.Discovery.Function.Models;

public sealed class WebSearchContext
{
    public string? Location { get; init; }
    public string CountryCode { get; init; } = "us";
    public string Language { get; init; } = "es";
}
