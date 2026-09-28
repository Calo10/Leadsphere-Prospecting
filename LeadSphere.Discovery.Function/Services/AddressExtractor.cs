using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace LeadSphere.Discovery.Function.Services;

internal static class AddressExtractor
{
    private static readonly Regex JsonLdScript = new(
        @"<script[^>]*type\s*=\s*[""']application/ld\+json[""'][^>]*>(.*?)</script>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex StreetToken = new(
        @"\b(street|st\.?|avenue|ave\.?|road|rd\.?|boulevard|blvd\.?|drive|dr\.?|lane|ln\.?|way|court|ct\.?|circle|cir\.?|highway|hwy\.?|pike|parkway|pkwy\.?|place|pl\.?|terrace|suite|ste\.?|floor|fl\.?|calle|avenida|carrera|carretera|boulevard|paseo)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex StreetLine = new(
        @"\b\d{1,6}[A-Za-z]?(?:\s+[A-Za-z0-9.#\-]+){1,8}\s+(?:Street|St\.?|Avenue|Ave\.?|Road|Rd\.?|Boulevard|Blvd\.?|Drive|Dr\.?|Lane|Ln\.?|Way|Court|Ct\.?|Circle|Cir\.?|Highway|Hwy\.?|Parkway|Pkwy\.?|Place|Pl\.?|Terrace|Calle|Avenida|Carrera|Carretera)\b(?:[^\n,]{0,40})?(?:,\s*[A-Za-z .'-]{2,40}){0,3}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> TooVague = new(StringComparer.OrdinalIgnoreCase)
    {
        "usa", "us", "united states", "united states of america", "florida", "texas", "california",
        "new york", "miami", "remote", "worldwide", "global", "n/a", "unknown"
    };

    public sealed record ExtractedAddress(string Formatted, string? Locality);

    public static ExtractedAddress? ExtractFromHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        return ExtractFromJsonLd(html)
            ?? ExtractFromMicrodata(html)
            ?? ExtractFromText(html);
    }

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var cleaned = Regex.Replace(WebUtility.HtmlDecode(value).Trim(), @"\s+", " ");
        cleaned = cleaned.Trim(' ', ',', ';', '|', '-');
        if (cleaned.Length is < 8 or > 500)
            return null;

        return LooksLikeStreetAddress(cleaned) ? cleaned : null;
    }

    public static bool LooksLikeStreetAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var cleaned = Regex.Replace(value.Trim(), @"\s+", " ");
        if (cleaned.Length < 8 || TooVague.Contains(cleaned))
            return false;

        if (!cleaned.Any(char.IsDigit))
            return false;

        return StreetToken.IsMatch(cleaned) || StreetLine.IsMatch(cleaned);
    }

    public static string? LocalityFromFormatted(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return null;

        var parts = address.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return null;

        var city = parts[^2];
        var region = parts[^1];
        if (city.Any(char.IsDigit) && parts.Length >= 3)
        {
            city = parts[^3];
            region = parts[^2];
        }

        var locality = Regex.Replace($"{city}, {region}", @"\s+", " ").Trim(' ', ',');
        return locality.Length is >= 3 and <= 120 ? locality : null;
    }

    private static ExtractedAddress? ExtractFromJsonLd(string html)
    {
        foreach (Match match in JsonLdScript.Matches(html))
        {
            var json = WebUtility.HtmlDecode(match.Groups[1].Value.Trim());
            if (string.IsNullOrWhiteSpace(json))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var found = WalkJson(doc.RootElement);
                if (found is not null)
                    return found;
            }
            catch (JsonException)
            {
                // Ignore malformed JSON-LD blocks.
            }
        }

        return null;
    }

    private static ExtractedAddress? WalkJson(JsonElement node)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray())
                {
                    var found = WalkJson(item);
                    if (found is not null)
                        return found;
                }
                break;

            case JsonValueKind.Object:
                if (node.TryGetProperty("address", out var addressNode))
                {
                    var fromAddress = ReadPostalAddress(addressNode) ?? WalkJson(addressNode);
                    if (fromAddress is not null)
                        return fromAddress;
                }

                if (IsPostalAddress(node))
                {
                    var postal = ReadPostalAddress(node);
                    if (postal is not null)
                        return postal;
                }

                if (node.TryGetProperty("@graph", out var graph))
                {
                    var fromGraph = WalkJson(graph);
                    if (fromGraph is not null)
                        return fromGraph;
                }

                foreach (var prop in node.EnumerateObject())
                {
                    if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        var nested = WalkJson(prop.Value);
                        if (nested is not null)
                            return nested;
                    }
                }
                break;
        }

        return null;
    }

    private static bool IsPostalAddress(JsonElement node)
    {
        if (!node.TryGetProperty("@type", out var type))
            return node.TryGetProperty("streetAddress", out _);

        return type.ValueKind == JsonValueKind.String
            && type.GetString()?.Contains("PostalAddress", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static ExtractedAddress? ReadPostalAddress(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.String)
        {
            var formatted = Normalize(node.GetString());
            return formatted is null ? null : new ExtractedAddress(formatted, LocalityFromFormatted(formatted));
        }

        if (node.ValueKind != JsonValueKind.Object)
            return null;

        var street = ReadString(node, "streetAddress");
        var locality = ReadString(node, "addressLocality");
        var region = ReadString(node, "addressRegion");
        var postal = ReadString(node, "postalCode");
        var country = ReadString(node, "addressCountry");

        var parts = new[] { street, locality, region, postal, country }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Trim())
            .ToList();
        if (parts.Count == 0)
            return null;

        var assembled = Normalize(string.Join(", ", parts));
        if (assembled is null && !string.IsNullOrWhiteSpace(street) && street.Any(char.IsDigit))
            assembled = string.Join(", ", parts);

        if (string.IsNullOrWhiteSpace(assembled) || !LooksLikeStreetAddress(assembled))
            return null;

        var city = string.Join(", ", new[] { locality, region }.Where(v => !string.IsNullOrWhiteSpace(v)));
        return new ExtractedAddress(assembled, string.IsNullOrWhiteSpace(city) ? LocalityFromFormatted(assembled) : city);
    }

    private static ExtractedAddress? ExtractFromMicrodata(string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        var street = FirstItemProp(document, "streetAddress");
        if (string.IsNullOrWhiteSpace(street))
            return null;

        var locality = FirstItemProp(document, "addressLocality");
        var region = FirstItemProp(document, "addressRegion");
        var postal = FirstItemProp(document, "postalCode");
        var country = FirstItemProp(document, "addressCountry");
        var formatted = Normalize(string.Join(", ", new[] { street, locality, region, postal, country }.Where(v => !string.IsNullOrWhiteSpace(v))));
        if (formatted is null)
            return null;

        var city = string.Join(", ", new[] { locality, region }.Where(v => !string.IsNullOrWhiteSpace(v)));
        return new ExtractedAddress(formatted, string.IsNullOrWhiteSpace(city) ? null : city);
    }

    private static ExtractedAddress? ExtractFromText(string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        foreach (var node in document.DocumentNode.SelectNodes("//script|//style|//noscript|//svg") ?? Enumerable.Empty<HtmlNode>())
            node.Remove();

        var text = WebUtility.HtmlDecode(document.DocumentNode.InnerText ?? string.Empty);
        var match = StreetLine.Match(text);
        if (!match.Success)
            return null;

        var formatted = Normalize(match.Value);
        return formatted is null ? null : new ExtractedAddress(formatted, LocalityFromFormatted(formatted));
    }

    private static string? FirstItemProp(HtmlDocument document, string name)
    {
        var node = document.DocumentNode.SelectSingleNode($"//*[@itemprop='{name}']");
        if (node is null)
            return null;

        var content = node.GetAttributeValue("content", null);
        var value = string.IsNullOrWhiteSpace(content) ? node.InnerText : content;
        value = Regex.Replace(WebUtility.HtmlDecode(value ?? string.Empty).Trim(), @"\s+", " ");
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? ReadString(JsonElement node, string name)
    {
        if (!node.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Object when value.TryGetProperty("name", out var nested) => nested.GetString(),
            _ => null
        };
    }
}
