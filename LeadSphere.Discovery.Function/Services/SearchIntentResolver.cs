using System.Text.RegularExpressions;
using LeadSphere.Discovery.Function.Models;

namespace LeadSphere.Discovery.Function.Services;

internal sealed class SearchIntent
{
    public string Industry { get; init; } = string.Empty;
    public string Profile { get; init; } = string.Empty;
    public string? Location { get; init; }
    public string? SerpApiLocation { get; init; }
    public string CountryCode { get; init; } = "us";
    public string Language { get; init; } = "es";
    public IReadOnlyList<string> IndustryKeywords { get; init; } = [];
    public IReadOnlyList<string> ProfileKeywords { get; init; } = [];
    public IReadOnlyList<string> BusinessTypeKeywords { get; init; } = [];
}

internal static class SearchIntentResolver
{
    private static readonly Regex LocationInTextRegex = new(
        @"(?:en|in|dentro de|located in|based in|within)\s+([A-Za-záéíóúñÁÉÍÓÚÑ][A-Za-záéíóúñÁÉÍÓÚÑ\s]{2,40}?)(?:\s*[,.\)]|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Dictionary<string, string> UsStateSerpLocations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["florida"] = "Florida,United States",
        ["california"] = "California,United States",
        ["texas"] = "Texas,United States",
        ["new york"] = "New York,United States",
        ["georgia"] = "Georgia,United States",
        ["arizona"] = "Arizona,United States",
        ["illinois"] = "Illinois,United States",
        ["colorado"] = "Colorado,United States",
        ["nevada"] = "Nevada,United States",
        ["washington"] = "Washington,United States"
    };

    private static readonly Dictionary<string, string[]> IndustryEnglishVariants = new(StringComparer.OrdinalIgnoreCase)
    {
        ["paqueteria"] = ["package shipping", "courier delivery", "parcel shipping"],
        ["paquetería"] = ["package shipping", "courier delivery", "parcel shipping"],
        ["envios"] = ["shipping", "delivery services", "parcel delivery"],
        ["envíos"] = ["shipping", "delivery services", "parcel delivery"],
        ["mensajeria"] = ["courier", "messenger delivery", "same day delivery"],
        ["mensajería"] = ["courier", "messenger delivery", "same day delivery"],
        ["logistica"] = ["logistics", "freight shipping", "transportation"],
        ["logística"] = ["logistics", "freight shipping", "transportation"],
        ["transporte"] = ["transportation", "freight carrier", "trucking company"]
    };

    public static SearchIntent Resolve(SearchRecord search)
    {
        var criteria = search.Criteria;
        var signals = search.FeedbackSignals;
        var profile = SearchFeedbackSignals.OriginalProfile(search.ProfileDescription);
        var notes = signals?.Notes;
        var keywordSource = string.Join(' ', new[] { profile, notes }.Where(v => !string.IsNullOrWhiteSpace(v)));
        var industry = FirstNonEmpty(criteria?.Industry, search.Name) ?? "companies";
        var location = FirstNonEmpty(
            criteria?.Location,
            ExtractLocationFromText(notes),
            ExtractLocationFromText(profile),
            ExtractLocationFromText(industry));

        var industryKeywords = Tokenize(industry).ToList();
        var profileKeywords = ExtractProfileKeywords(keywordSource);
        var businessTypeKeywords = DetectBusinessTypeKeywords(industry, keywordSource);

        return new SearchIntent
        {
            Industry = industry,
            Profile = profile,
            Location = location,
            SerpApiLocation = ResolveSerpApiLocation(location),
            CountryCode = ResolveCountryCode(location, keywordSource),
            Language = DetectLanguage(industry, keywordSource),
            IndustryKeywords = industryKeywords,
            ProfileKeywords = profileKeywords,
            BusinessTypeKeywords = businessTypeKeywords
        };
    }

    public static IReadOnlyList<string> EnglishIndustryVariants(string industry)
    {
        var lower = industry.ToLowerInvariant();
        foreach (var (key, variants) in IndustryEnglishVariants)
        {
            if (lower.Contains(key, StringComparison.Ordinal))
                return variants;
        }

        return [];
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static string? ExtractLocationFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var match = LocationInTextRegex.Match(text);
        if (match.Success)
            return NormalizeLocation(match.Groups[1].Value.Trim());

        foreach (var state in UsStateSerpLocations.Keys)
        {
            if (text.Contains(state, StringComparison.OrdinalIgnoreCase))
                return NormalizeLocation(state);
        }

        if (text.Contains("mexico", StringComparison.OrdinalIgnoreCase) || text.Contains("méxico", StringComparison.OrdinalIgnoreCase))
            return "México";

        if (text.Contains("colombia", StringComparison.OrdinalIgnoreCase))
            return "Colombia";

        return null;
    }

    private static string NormalizeLocation(string location)
    {
        location = Regex.Replace(location.Trim(), @"\s+", " ");
        return location;
    }

    private static string? ResolveSerpApiLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
            return null;

        var cleaned = Regex.Replace(location.Trim(), @"\s+", " ");
        if (UsStateSerpLocations.TryGetValue(cleaned, out var exact))
            return exact;

        var withoutCountry = Regex.Replace(
            cleaned,
            @"\s*,\s*(united states|u\.s\.a\.?|usa|u\.s\.?|us)$",
            string.Empty,
            RegexOptions.IgnoreCase).Trim();
        if (withoutCountry.Length > 0 && UsStateSerpLocations.TryGetValue(withoutCountry, out var stateOnly))
            return stateOnly;

        foreach (var (key, value) in UsStateSerpLocations)
        {
            if (Regex.IsMatch(cleaned, $@"\b{Regex.Escape(key)}\b", RegexOptions.IgnoreCase))
                return value;
        }

        if (Regex.IsMatch(cleaned, @"\b(united states|u\.s\.a\.?|usa|u\.s\.?)\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(cleaned, @"\bus\b", RegexOptions.IgnoreCase))
            return "United States";

        if (cleaned.Contains("mexico", StringComparison.OrdinalIgnoreCase)
            || cleaned.Contains("méxico", StringComparison.OrdinalIgnoreCase))
            return "Mexico";

        // SerpAPI 400s on unknown location strings — omit and rely on gl instead.
        return null;
    }

    private static string ResolveCountryCode(string? location, string profile)
    {
        var combined = $"{location} {profile}".ToLowerInvariant();
        if (combined.Contains("mexico") || combined.Contains("méxico"))
            return "mx";
        if (combined.Contains("colombia"))
            return "co";
        if (combined.Contains("spain") || combined.Contains("españa"))
            return "es";
        return "us";
    }

    private static string DetectLanguage(string industry, string profile)
    {
        var text = $"{industry} {profile}";
        return Regex.IsMatch(text, @"[áéíóúñ]", RegexOptions.IgnoreCase) ? "es" : "en";
    }

    private static IReadOnlyList<string> DetectBusinessTypeKeywords(string industry, string profile)
    {
        var text = $"{industry} {profile}".ToLowerInvariant();
        var keywords = new List<string>();

        void AddIfMatch(string trigger, params string[] terms)
        {
            if (text.Contains(trigger, StringComparison.Ordinal))
                keywords.AddRange(terms);
        }

        AddIfMatch("paqueter", "paqueteria", "paquete", "package", "parcel", "courier");
        AddIfMatch("envio", "envio", "envios", "shipping", "delivery", "shipment");
        AddIfMatch("mensajer", "mensajeria", "messenger", "courier");
        AddIfMatch("logistic", "logistics", "logistica", "freight", "cargo");
        AddIfMatch("transport", "transport", "transporte", "trucking", "carrier");

        return keywords.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IReadOnlyList<string> ExtractProfileKeywords(string profile)
    {
        if (string.IsNullOrWhiteSpace(profile))
            return [];

        return Regex.Matches(profile.ToLowerInvariant(), @"\b[a-záéíóúñ]{4,}\b")
            .Select(m => m.Value)
            .Where(w => w is not (
                "empresas" or "empresa" or "that" or "with" or "from" or "this" or "have" or
                "para" or "como" or "todo" or "tipo" or "dentro" or "muevan" or "tipos" or
                "thumbs" or "feedback" or "prefer" or "reject" or "follow" or "notes"))
            .Distinct()
            .Take(12)
            .ToList();
    }

    private static IEnumerable<string> Tokenize(string value) =>
        value.ToLowerInvariant()
            .Split([' ', ',', '-', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 2);
}
