using System.Text;
using System.Text.RegularExpressions;
using LeadSphere.Discovery.Function.Models;

namespace LeadSphere.Discovery.Function.Services;

internal sealed record GeoPlace(
    string Canonical,
    string? City,
    string? Region,
    string? RegionCode,
    string Country,
    string CountryCode,
    LocationGranularity Granularity,
    string[] Aliases,
    string[] ZipCodes,
    string[] ChildTokens,
    string[] ParentTokens,
    string? SerpApiLocation);

internal static class LocationGazetteer
{
    private static readonly Regex NonLetters = new(@"[^\p{L}\p{Nd}]+", RegexOptions.Compiled);
    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "el", "la", "los", "las", "un", "una", "en", "de", "del", "al", "y", "o",
        "the", "in", "at", "of", "on", "near", "around", "within", "inside",
        "area", "zona", "ciudad", "city", "town", "located", "based", "from"
    };

    private static readonly IReadOnlyList<GeoPlace> Places = BuildPlaces();

    public static IReadOnlyList<GeoPlace> All => Places;

    public static string Fold(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Trim().Normalize(NormalizationForm.FormD);
        var chars = normalized
            .Where(c => char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .ToArray();
        return new string(chars).Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    public static IReadOnlyList<string> SignificantTokens(string? value)
    {
        var folded = Fold(value);
        if (string.IsNullOrWhiteSpace(folded))
            return [];

        return NonLetters.Split(folded)
            .Where(t => t.Length > 1 && !Stopwords.Contains(t))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public static string CleanQuery(string? value)
    {
        var tokens = SignificantTokens(value);
        return string.Join(' ', tokens);
    }

    public static GeoPlace? FindBest(string? raw)
    {
        var tokens = SignificantTokens(raw);
        if (tokens.Count == 0)
            return null;

        var folded = Fold(raw);
        GeoPlace? best = null;
        var bestScore = 0;

        foreach (var place in Places)
        {
            var score = Score(place, tokens, folded);
            if (score <= 0)
                continue;

            if (best is null
                || score > bestScore
                || (score == bestScore && place.Granularity < best.Granularity))
            {
                best = place;
                bestScore = score;
            }
        }

        return best;
    }

    public static GeoPlace? FindByName(string? name)
    {
        var folded = Fold(name);
        if (string.IsNullOrWhiteSpace(folded))
            return null;

        return Places.FirstOrDefault(p =>
            Fold(p.Canonical) == folded
            || p.Aliases.Any(a => Fold(a) == folded));
    }

    public static bool MentionsOtherUsState(string haystack, string? allowedRegion, string? allowedRegionCode)
    {
        var folded = Fold(haystack);
        foreach (var place in Places.Where(p => p.Granularity == LocationGranularity.State && p.CountryCode == "us"))
        {
            if (string.Equals(place.Region, allowedRegion, StringComparison.OrdinalIgnoreCase)
                || string.Equals(place.RegionCode, allowedRegionCode, StringComparison.OrdinalIgnoreCase))
                continue;

            if (ContainsToken(folded, Fold(place.Canonical))
                || (!string.IsNullOrWhiteSpace(place.RegionCode) && ContainsStateCode(haystack, place.RegionCode)))
                return true;
        }

        return false;
    }

    public static bool ContainsToken(string foldedHaystack, string foldedNeedle)
    {
        if (string.IsNullOrWhiteSpace(foldedNeedle))
            return false;

        if (foldedNeedle.Contains(' '))
            return foldedHaystack.Contains(foldedNeedle, StringComparison.Ordinal);

        return Regex.IsMatch(foldedHaystack, $@"\b{Regex.Escape(foldedNeedle)}\b", RegexOptions.IgnoreCase);
    }

    public static bool ContainsStateCode(string haystack, string code)
    {
        return Regex.IsMatch(haystack, $@"(?:^|[\s,;|/]){Regex.Escape(code)}(?:$|[\s,;|/])", RegexOptions.IgnoreCase);
    }

    private static int Score(GeoPlace place, IReadOnlyList<string> tokens, string folded)
    {
        var score = 0;
        foreach (var alias in place.Aliases.Concat([place.Canonical]))
        {
            var aliasFold = Fold(alias);
            if (string.IsNullOrWhiteSpace(aliasFold))
                continue;

            if (tokens.Contains(aliasFold) || ContainsToken(folded, aliasFold))
                score += aliasFold.Contains(' ') ? 6 : aliasFold.Length >= 5 ? 5 : 3;
        }

        if (score == 0)
            return 0;

        score += place.Granularity switch
        {
            LocationGranularity.Neighborhood => 8,
            LocationGranularity.City => 6,
            LocationGranularity.Metro => 4,
            LocationGranularity.State => 2,
            LocationGranularity.Country => 1,
            _ => 0
        };

        return score;
    }

    private static IReadOnlyList<GeoPlace> BuildPlaces()
    {
        var floridaCities = new[]
        {
            "miami", "doral", "hialeah", "coral gables", "miami beach", "miami gardens", "homestead",
            "kendall", "aventura", "sweetwater", "medley", "miami lakes", "hialeah gardens",
            "fort lauderdale", "hollywood", "pembroke pines", "miramar", "davie", "plantation",
            "sunrise", "boca raton", "west palm beach", "orlando", "tampa", "jacksonville",
            "tallahassee", "naples", "fort myers", "sarasota", "clearwater", "st petersburg",
            "kissimmee", "gainesville", "pensacola", "daytona beach", "cape coral", "key west",
            "brickell", "wynwood", "coconut grove", "little havana", "downtown miami"
        };

        var miamiChildren = new[]
        {
            "doral", "el doral", "hialeah", "coral gables", "miami beach", "miami gardens",
            "homestead", "kendall", "aventura", "sweetwater", "medley", "miami lakes",
            "brickell", "wynwood", "coconut grove", "little havana", "downtown miami",
            "miami-dade", "miami dade"
        };

        var places = new List<GeoPlace>
        {
            Place("Doral, FL", "Doral", "Florida", "FL", "United States", "us", LocationGranularity.City,
                ["doral", "el doral"],
                ["33122", "33166", "33172", "33178", "33182"],
                [],
                ["miami", "miami-dade", "miami dade", "florida", "fl"],
                "Doral,Florida,United States"),
            Place("Brickell, Miami, FL", "Brickell", "Florida", "FL", "United States", "us", LocationGranularity.Neighborhood,
                ["brickell"], [], [], ["miami", "florida", "fl"], "Miami,Florida,United States"),
            Place("Wynwood, Miami, FL", "Wynwood", "Florida", "FL", "United States", "us", LocationGranularity.Neighborhood,
                ["wynwood"], [], [], ["miami", "florida", "fl"], "Miami,Florida,United States"),
            Place("Coral Gables, FL", "Coral Gables", "Florida", "FL", "United States", "us", LocationGranularity.City,
                ["coral gables"], [], [], ["miami", "florida", "fl"], "Coral Gables,Florida,United States"),
            Place("Miami Beach, FL", "Miami Beach", "Florida", "FL", "United States", "us", LocationGranularity.City,
                ["miami beach"], [], [], ["miami", "florida", "fl"], "Miami Beach,Florida,United States"),
            Place("Hialeah, FL", "Hialeah", "Florida", "FL", "United States", "us", LocationGranularity.City,
                ["hialeah"], [], [], ["miami", "florida", "fl"], "Hialeah,Florida,United States"),
            Place("Fort Lauderdale, FL", "Fort Lauderdale", "Florida", "FL", "United States", "us", LocationGranularity.City,
                ["fort lauderdale", "ft lauderdale"], [], [], ["florida", "fl"], "Fort Lauderdale,Florida,United States"),
            Place("Orlando, FL", "Orlando", "Florida", "FL", "United States", "us", LocationGranularity.City,
                ["orlando"], [], [], ["florida", "fl"], "Orlando,Florida,United States"),
            Place("Tampa, FL", "Tampa", "Florida", "FL", "United States", "us", LocationGranularity.City,
                ["tampa"], [], [], ["florida", "fl"], "Tampa,Florida,United States"),
            Place("Jacksonville, FL", "Jacksonville", "Florida", "FL", "United States", "us", LocationGranularity.City,
                ["jacksonville"], [], [], ["florida", "fl"], "Jacksonville,Florida,United States"),
            Place("Miami, FL", "Miami", "Florida", "FL", "United States", "us", LocationGranularity.Metro,
                ["miami", "miami-dade", "miami dade", "south florida"],
                [],
                miamiChildren,
                ["florida", "fl"],
                "Miami,Florida,United States"),
            Place("Florida, United States", null, "Florida", "FL", "United States", "us", LocationGranularity.State,
                ["florida", "fl", "fla"],
                [],
                floridaCities,
                ["united states", "usa", "us"],
                "Florida,United States"),
            Place("New York, NY", "New York", "New York", "NY", "United States", "us", LocationGranularity.Metro,
                ["new york", "nyc", "new york city"], [], ["brooklyn", "manhattan", "queens", "bronx"], ["new york", "ny"],
                "New York,New York,United States"),
            Place("Los Angeles, CA", "Los Angeles", "California", "CA", "United States", "us", LocationGranularity.Metro,
                ["los angeles", "la"], [], [], ["california", "ca"], "Los Angeles,California,United States"),
            Place("Houston, TX", "Houston", "Texas", "TX", "United States", "us", LocationGranularity.City,
                ["houston"], [], [], ["texas", "tx"], "Houston,Texas,United States"),
            Place("Dallas, TX", "Dallas", "Texas", "TX", "United States", "us", LocationGranularity.City,
                ["dallas"], [], [], ["texas", "tx"], "Dallas,Texas,United States"),
            Place("Austin, TX", "Austin", "Texas", "TX", "United States", "us", LocationGranularity.City,
                ["austin"], [], [], ["texas", "tx"], "Austin,Texas,United States"),
            Place("Chicago, IL", "Chicago", "Illinois", "IL", "United States", "us", LocationGranularity.City,
                ["chicago"], [], [], ["illinois", "il"], "Chicago,Illinois,United States"),
            Place("Atlanta, GA", "Atlanta", "Georgia", "GA", "United States", "us", LocationGranularity.City,
                ["atlanta"], [], [], ["georgia", "ga"], "Atlanta,Georgia,United States"),
            Place("Mexico", null, null, null, "Mexico", "mx", LocationGranularity.Country,
                ["mexico", "méxico", "mx"], [], ["cdmx", "ciudad de mexico", "guadalajara", "monterrey"], [], "Mexico"),
            Place("Colombia", null, null, null, "Colombia", "co", LocationGranularity.Country,
                ["colombia"], [], ["bogota", "medellin", "cali"], [], "Colombia"),
            Place("Spain", null, null, null, "Spain", "es", LocationGranularity.Country,
                ["spain", "españa", "espana"], [], ["madrid", "barcelona", "valencia"], [], "Spain"),
            Place("United States", null, null, null, "United States", "us", LocationGranularity.Country,
                ["united states", "usa", "u.s.", "u.s.a", "estados unidos"], [], [], [], "United States")
        };

        foreach (var state in UsStates)
        {
            if (places.Any(p => string.Equals(p.RegionCode, state.Code, StringComparison.OrdinalIgnoreCase)
                                && p.Granularity == LocationGranularity.State))
                continue;

            places.Add(Place(
                $"{state.Name}, United States",
                null,
                state.Name,
                state.Code,
                "United States",
                "us",
                LocationGranularity.State,
                [state.Name, state.Code],
                [],
                [],
                ["united states", "usa", "us"],
                $"{state.Name},United States"));
        }

        return places;
    }

    private static GeoPlace Place(
        string canonical,
        string? city,
        string? region,
        string? regionCode,
        string country,
        string countryCode,
        LocationGranularity granularity,
        string[] aliases,
        string[] zips,
        string[] children,
        string[] parents,
        string? serp) =>
        new(canonical, city, region, regionCode, country, countryCode, granularity, aliases, zips, children, parents, serp);

    private static readonly (string Name, string Code)[] UsStates =
    [
        ("Alabama", "AL"), ("Alaska", "AK"), ("Arizona", "AZ"), ("Arkansas", "AR"),
        ("California", "CA"), ("Colorado", "CO"), ("Connecticut", "CT"), ("Delaware", "DE"),
        ("Georgia", "GA"), ("Hawaii", "HI"), ("Idaho", "ID"), ("Illinois", "IL"),
        ("Indiana", "IN"), ("Iowa", "IA"), ("Kansas", "KS"), ("Kentucky", "KY"),
        ("Louisiana", "LA"), ("Maine", "ME"), ("Maryland", "MD"), ("Massachusetts", "MA"),
        ("Michigan", "MI"), ("Minnesota", "MN"), ("Mississippi", "MS"), ("Missouri", "MO"),
        ("Montana", "MT"), ("Nebraska", "NE"), ("Nevada", "NV"), ("New Hampshire", "NH"),
        ("New Jersey", "NJ"), ("New Mexico", "NM"), ("New York", "NY"), ("North Carolina", "NC"),
        ("North Dakota", "ND"), ("Ohio", "OH"), ("Oklahoma", "OK"), ("Oregon", "OR"),
        ("Pennsylvania", "PA"), ("Rhode Island", "RI"), ("South Carolina", "SC"),
        ("South Dakota", "SD"), ("Tennessee", "TN"), ("Texas", "TX"), ("Utah", "UT"),
        ("Vermont", "VT"), ("Virginia", "VA"), ("Washington", "WA"), ("West Virginia", "WV"),
        ("Wisconsin", "WI"), ("Wyoming", "WY"), ("District of Columbia", "DC")
    ];
}
