using System.Collections.Concurrent;
using System.Text.Json;
using LeadSphere.Discovery.Function.Models;
using LeadSphere.Discovery.Function.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeadSphere.Discovery.Function.Services;

public interface ILocationResolutionService
{
    Task<ResolvedSearchLocation?> ResolveAsync(string? raw, CancellationToken cancellationToken);
}

public sealed class LocationResolutionService : ILocationResolutionService
{
    private static readonly ConcurrentDictionary<string, ResolvedSearchLocation?> Cache = new(StringComparer.OrdinalIgnoreCase);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly WebSearchOptions _options;
    private readonly ILogger<LocationResolutionService> _logger;

    public LocationResolutionService(
        IHttpClientFactory httpClientFactory,
        IOptions<WebSearchOptions> options,
        ILogger<LocationResolutionService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ResolvedSearchLocation?> ResolveAsync(string? raw, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var key = LocationGazetteer.Fold(raw);
        if (Cache.TryGetValue(key, out var cached))
            return cached;

        var resolved = await ResolveCoreAsync(raw, cancellationToken);
        Cache[key] = resolved;
        return resolved;
    }

    private async Task<ResolvedSearchLocation?> ResolveCoreAsync(string raw, CancellationToken cancellationToken)
    {
        var local = LocationGazetteer.FindBest(raw);
        var serp = await TryResolveSerpApiAsync(raw, local, cancellationToken);

        if (local is null && serp is null)
        {
            var cleaned = LocationGazetteer.CleanQuery(raw);
            if (string.IsNullOrWhiteSpace(cleaned))
                return null;

            _logger.LogInformation("Location '{Raw}' could not be geocoded; using cleaned tokens '{Cleaned}'.", raw, cleaned);
            return new ResolvedSearchLocation
            {
                Raw = raw.Trim(),
                QueryLabel = ToTitle(cleaned),
                MatchTokens = LocationGazetteer.SignificantTokens(cleaned),
                Granularity = LocationGranularity.City,
                CountryCode = "us"
            };
        }

        var place = local;
        var serpName = serp?.CanonicalName;
        var countryCode = place?.CountryCode ?? serp?.CountryCode ?? "us";
        var granularity = place?.Granularity
            ?? MapTargetType(serp?.TargetType)
            ?? LocationGranularity.City;

        var queryLabel = place?.Canonical
            ?? serp?.DisplayName
            ?? LocationGazetteer.CleanQuery(raw);

        var matchTokens = new List<string>();
        if (place is not null)
        {
            matchTokens.AddRange(place.Aliases.Select(LocationGazetteer.Fold));
            if (!string.IsNullOrWhiteSpace(place.City))
                matchTokens.Add(LocationGazetteer.Fold(place.City));
        }

        if (serp is not null)
        {
            foreach (var token in LocationGazetteer.SignificantTokens(serp.DisplayName))
            {
                if (token is not ("united" or "states" or "usa"))
                    matchTokens.Add(token);
            }
        }

        var resolved = new ResolvedSearchLocation
        {
            Raw = raw.Trim(),
            QueryLabel = queryLabel,
            SerpApiLocation = place?.SerpApiLocation ?? serpName,
            City = place?.City ?? FirstCity(serp?.DisplayName),
            Region = place?.Region,
            RegionCode = place?.RegionCode,
            Country = place?.Country ?? CountryFromCode(countryCode),
            CountryCode = countryCode,
            Granularity = granularity,
            MatchTokens = matchTokens.Where(t => t.Length > 1).Distinct(StringComparer.Ordinal).ToList(),
            ZipCodes = place?.ZipCodes ?? [],
            ChildTokens = place?.ChildTokens.Select(LocationGazetteer.Fold).ToList() ?? [],
            ParentTokens = place?.ParentTokens.Select(LocationGazetteer.Fold).ToList() ?? []
        };

        _logger.LogInformation(
            "Resolved location '{Raw}' → {Label} ({Granularity}, serp={Serp})",
            raw,
            resolved.QueryLabel,
            resolved.Granularity,
            resolved.SerpApiLocation ?? "none");

        return resolved;
    }

    private async Task<SerpLocationHit?> TryResolveSerpApiAsync(
        string raw,
        GeoPlace? local,
        CancellationToken cancellationToken)
    {
        var queries = new[]
        {
            local?.Canonical,
            LocationGazetteer.CleanQuery(raw),
            raw
        }.Where(q => !string.IsNullOrWhiteSpace(q)).Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var query in queries)
        {
            var hit = await LookupSerpApiAsync(query!, cancellationToken);
            if (hit is not null)
                return hit;
        }

        return null;
    }

    private async Task<SerpLocationHit?> LookupSerpApiAsync(string query, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://serpapi.com/locations.json?q={Uri.EscapeDataString(query)}&limit=8";
            if (!string.IsNullOrWhiteSpace(_options.SerpApi.ApiKey))
                url += $"&api_key={Uri.EscapeDataString(_options.SerpApi.ApiKey)}";

            var client = _httpClientFactory.CreateClient("WebSearch");
            using var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            SerpLocationHit? best = null;
            var bestScore = -1;
            var wanted = LocationGazetteer.SignificantTokens(query);

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                var canonical = item.TryGetProperty("canonical_name", out var canEl) ? canEl.GetString() : name;
                var type = item.TryGetProperty("target_type", out var typeEl) ? typeEl.GetString() : null;
                var country = item.TryGetProperty("country_code", out var ccEl) ? ccEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(canonical))
                    continue;

                var score = ScoreSerpHit(name ?? canonical!, type, wanted);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = new SerpLocationHit(canonical ?? name!, name ?? canonical!, type, country);
                }
            }

            return bestScore >= 3 ? best : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SerpAPI locations lookup failed for '{Query}'", query);
            return null;
        }
    }

    private static int ScoreSerpHit(string name, string? type, IReadOnlyList<string> wanted)
    {
        var tokens = LocationGazetteer.SignificantTokens(name);
        var overlap = wanted.Count(t => tokens.Contains(t, StringComparer.OrdinalIgnoreCase));
        var typeScore = type?.ToLowerInvariant() switch
        {
            "neighborhood" => 5,
            "city" or "municipality" => 4,
            "county" or "dma region" => 3,
            "state" or "province" => 2,
            "country" => 1,
            _ => 2
        };
        return overlap * 4 + typeScore;
    }

    private static LocationGranularity? MapTargetType(string? type) =>
        type?.ToLowerInvariant() switch
        {
            "neighborhood" => LocationGranularity.Neighborhood,
            "city" or "municipality" or "postal code" => LocationGranularity.City,
            "county" or "dma region" => LocationGranularity.Metro,
            "state" or "province" => LocationGranularity.State,
            "country" => LocationGranularity.Country,
            _ => null
        };

    private static string? FirstCity(string? display)
    {
        if (string.IsNullOrWhiteSpace(display))
            return null;
        return display.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    }

    private static string CountryFromCode(string code) =>
        code.ToLowerInvariant() switch
        {
            "mx" => "Mexico",
            "co" => "Colombia",
            "es" => "Spain",
            _ => "United States"
        };

    private static string ToTitle(string value) =>
        string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));

    private sealed record SerpLocationHit(string CanonicalName, string DisplayName, string? TargetType, string? CountryCode);
}

internal static class LocationConstraint
{
    public static bool Matches(
        ResolvedSearchLocation? geo,
        string? address,
        string? location,
        params string?[] extra)
    {
        if (geo is null || !geo.HasConstraint)
            return true;

        var haystack = LocationGazetteer.Fold(string.Join(' ',
            new[] { address, location }.Concat(extra).Where(v => !string.IsNullOrWhiteSpace(v))));

        if (string.IsNullOrWhiteSpace(haystack))
            return geo.Granularity >= LocationGranularity.State;

        if (geo.CountryCode == "us"
            && LocationGazetteer.MentionsOtherUsState(haystack, geo.Region, geo.RegionCode))
            return false;

        if (HasPrimaryMatch(geo, haystack, address, location))
            return true;

        if (HasChildMatch(geo, haystack))
            return true;

        if (geo.Granularity >= LocationGranularity.State && HasRegionMatch(geo, haystack, address, location))
            return true;

        return false;
    }

    public static string ConstraintLabel(ResolvedSearchLocation? geo)
    {
        if (geo is null || !geo.HasConstraint)
            return string.Empty;

        return geo.Granularity switch
        {
            LocationGranularity.Neighborhood or LocationGranularity.City =>
                $"ONLY companies physically located in {geo.QueryLabel}. A parent city alone is not enough if the company is clearly in another neighborhood or city.",
            LocationGranularity.Metro =>
                $"ONLY companies in the {geo.QueryLabel} metro area, including nearby cities in the same metro.",
            LocationGranularity.State =>
                $"ONLY companies located in {geo.Region ?? geo.QueryLabel}. Other states are out of scope.",
            LocationGranularity.Country =>
                $"ONLY companies located in {geo.Country ?? geo.QueryLabel}.",
            _ => $"Prefer companies in {geo.QueryLabel}."
        };
    }

    private static bool HasPrimaryMatch(
        ResolvedSearchLocation geo,
        string foldedHaystack,
        string? address,
        string? location)
    {
        var skipParents = geo.Granularity <= LocationGranularity.City;
        foreach (var token in geo.MatchTokens)
        {
            if (skipParents && geo.ParentTokens.Contains(token, StringComparer.OrdinalIgnoreCase))
                continue;
            if (LocationGazetteer.ContainsToken(foldedHaystack, token))
                return true;
        }

        foreach (var zip in geo.ZipCodes)
        {
            if (!string.IsNullOrWhiteSpace(address) && address.Contains(zip, StringComparison.Ordinal))
                return true;
            if (!string.IsNullOrWhiteSpace(location) && location.Contains(zip, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool HasChildMatch(ResolvedSearchLocation geo, string foldedHaystack)
    {
        if (geo.Granularity is not (LocationGranularity.Metro or LocationGranularity.State or LocationGranularity.Country))
            return false;

        return geo.ChildTokens.Any(child => LocationGazetteer.ContainsToken(foldedHaystack, child));
    }

    private static bool HasRegionMatch(ResolvedSearchLocation geo, string foldedHaystack, string? address, string? location)
    {
        if (!string.IsNullOrWhiteSpace(geo.Region)
            && LocationGazetteer.ContainsToken(foldedHaystack, LocationGazetteer.Fold(geo.Region)))
            return true;

        if (!string.IsNullOrWhiteSpace(geo.RegionCode)
            && (LocationGazetteer.ContainsStateCode(address ?? "", geo.RegionCode)
                || LocationGazetteer.ContainsStateCode(location ?? "", geo.RegionCode)
                || LocationGazetteer.ContainsStateCode(foldedHaystack, geo.RegionCode)))
            return true;

        if (!string.IsNullOrWhiteSpace(geo.Country)
            && LocationGazetteer.ContainsToken(foldedHaystack, LocationGazetteer.Fold(geo.Country)))
            return true;

        return false;
    }
}
