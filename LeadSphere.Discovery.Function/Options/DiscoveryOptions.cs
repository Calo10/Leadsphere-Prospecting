namespace LeadSphere.Discovery.Function.Options;

public sealed class DiscoveryOptions
{
    public const string SectionName = "Discovery";

    public int MaxCompaniesPerSearch { get; set; } = 25;
    public int MaxSearchQueries { get; set; } = 6;
    public int MaxResultsPerSearchQuery { get; set; } = 20;
    public int MaxContactsPerCompany { get; set; } = 10;
    public int MaxLinkedInPeopleQueriesPerCompany { get; set; } = 6;
    public bool EnableExternalProfileSearch { get; set; } = true;
    public bool EnableCompanyMarketData { get; set; } = true;
    public int MaxCompanyNewsItems { get; set; } = 5;
    public bool EnableContactWebEnrichment { get; set; } = true;
    public bool PreferContactsWithEmailOrPhone { get; set; } = true;
    public int MaxEnrichmentQueriesPerCompany { get; set; } = 4;
    public double MinIndustryRelevanceScore { get; set; } = 0.20;
    public double MinCompanyFitScore { get; set; } = 0.45;
}

public sealed class WebSearchOptions
{
    public const string SectionName = "WebSearch";

    /// <summary>SerpApi | Google | Bing</summary>
    public string Provider { get; set; } = "SerpApi";

    public SerpApiOptions SerpApi { get; set; } = new();
    public GoogleSearchOptions Google { get; set; } = new();
    public BingSearchOptions Bing { get; set; } = new();
}

public sealed class SerpApiOptions
{
    public string ApiKey { get; set; } = string.Empty;
}

public sealed class GoogleSearchOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string SearchEngineId { get; set; } = string.Empty;
}

public sealed class BingSearchOptions
{
    public string ApiKey { get; set; } = string.Empty;
}

public sealed class OpenAiOptions
{
    public const string SectionName = "OpenAI";

    /// <summary>Azure | OpenAI</summary>
    public string Provider { get; set; } = "Azure";

    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "gpt-4o-mini";
    public string BaseUrl { get; set; } = "https://api.openai.com/v1/";
    public string Endpoint { get; set; } = "https://nexa-open-ai.openai.azure.com";
    public string Deployment { get; set; } = "whatsapp-bot";
    public string ApiVersion { get; set; } = "2025-01-01-preview";
}
