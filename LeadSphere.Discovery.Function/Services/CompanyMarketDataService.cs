using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LeadSphere.Discovery.Function.Models;
using LeadSphere.Discovery.Function.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeadSphere.Discovery.Function.Services;

public interface ICompanyMarketDataService
{
    Task ApplyAsync(
        CompanyEnrichmentData enrichment,
        string companyName,
        string? domain,
        string countryCode,
        string language,
        CancellationToken cancellationToken);
}

public sealed class CompanyMarketDataService : ICompanyMarketDataService
{
    private static readonly Regex SuffixRegex = new(
        @"\b(Inc|LLC|Ltd|S\.A\.|SA|Corp|Corporation|Company|Co|Holdings|Group|PLC|NV|AG|GmbH)\b\.?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> StopTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "de", "del", "la", "el", "los", "las", "of", "for"
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DiscoveryOptions _options;
    private readonly ILogger<CompanyMarketDataService> _logger;

    public CompanyMarketDataService(
        IHttpClientFactory httpClientFactory,
        IOptions<DiscoveryOptions> options,
        ILogger<CompanyMarketDataService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task ApplyAsync(
        CompanyEnrichmentData enrichment,
        string companyName,
        string? domain,
        string countryCode,
        string language,
        CancellationToken cancellationToken)
    {
        if (!_options.EnableCompanyMarketData)
            return;

        var cleanName = CleanCompanyName(companyName);
        if (string.IsNullOrWhiteSpace(cleanName))
            return;

        try
        {
            enrichment.News = await FetchNewsAsync(cleanName, domain, countryCode, language, cancellationToken);
            if (enrichment.News.Count > 0)
                enrichment.EnrichmentSources.Add("google-news");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Company news lookup failed for {Company}", cleanName);
        }

        try
        {
            var quote = await FetchStockQuoteAsync(cleanName, domain, cancellationToken);
            if (quote is null)
                return;

            enrichment.Ticker = quote.Ticker;
            enrichment.StockPrice = quote.Price;
            enrichment.StockChangePercent = quote.ChangePercent;
            enrichment.StockCurrency = quote.Currency;
            enrichment.StockAsOf = quote.AsOf;
            enrichment.EnrichmentSources.Add("yahoo-finance");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Stock quote lookup failed for {Company}", cleanName);
        }
    }

    private async Task<List<CompanyNewsItem>> FetchNewsAsync(
        string companyName,
        string? domain,
        string countryCode,
        string language,
        CancellationToken cancellationToken)
    {
        var query = $"\"{companyName}\"";
        if (!string.IsNullOrWhiteSpace(domain))
            query += $" OR {domain}";

        var hl = string.Equals(language, "es", StringComparison.OrdinalIgnoreCase) ? "es" : "en";
        var gl = string.IsNullOrWhiteSpace(countryCode) ? "us" : countryCode.Trim().ToLowerInvariant();
        var url =
            $"https://news.google.com/rss/search?q={Uri.EscapeDataString(query + " when:30d")}&hl={hl}&gl={gl}&ceid={gl}:{hl}";

        var xml = await GetStringAsync(url, cancellationToken);
        if (string.IsNullOrWhiteSpace(xml))
            return [];

        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse Google News RSS for {Company}", companyName);
            return [];
        }

        var items = new List<CompanyNewsItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in document.Descendants("item"))
        {
            var title = item.Element("title")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(title) || !seen.Add(title))
                continue;

            var link = item.Element("link")?.Value?.Trim();
            var source = item.Element("source")?.Value?.Trim();
            DateTimeOffset? publishedAt = null;
            if (DateTimeOffset.TryParse(item.Element("pubDate")?.Value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var parsed))
                publishedAt = parsed;

            items.Add(new CompanyNewsItem
            {
                Title = WebUtility.HtmlDecode(title),
                Url = string.IsNullOrWhiteSpace(link) ? null : link,
                Source = string.IsNullOrWhiteSpace(source) ? ExtractSourceFromTitle(title) : source,
                PublishedAt = publishedAt,
                ImageUrl = ExtractRssImageUrl(item)
            });

            if (items.Count >= Math.Clamp(_options.MaxCompanyNewsItems, 1, 10))
                break;
        }

        await AttachYahooThumbnailsAsync(items, companyName, cancellationToken);
        return items;
    }

    private async Task<StockQuote?> FetchStockQuoteAsync(
        string companyName,
        string? domain,
        CancellationToken cancellationToken)
    {
        var ticker = await ResolveTickerAsync(companyName, domain, cancellationToken);
        if (string.IsNullOrWhiteSpace(ticker))
            return null;

        var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(ticker)}?range=1d&interval=1d";
        using var document = await GetJsonAsync(url, cancellationToken);
        if (document is null)
            return null;

        if (!document.RootElement.TryGetProperty("chart", out var chart)
            || !chart.TryGetProperty("result", out var results)
            || results.ValueKind != JsonValueKind.Array
            || results.GetArrayLength() == 0)
            return null;

        var meta = results[0].GetProperty("meta");
        if (!meta.TryGetProperty("regularMarketPrice", out var priceElement)
            || priceElement.ValueKind != JsonValueKind.Number)
            return null;

        var price = priceElement.GetDecimal();
        decimal? changePercent = null;
        if (meta.TryGetProperty("chartPreviousClose", out var previous)
            && previous.ValueKind == JsonValueKind.Number)
        {
            var previousClose = previous.GetDecimal();
            if (previousClose != 0)
                changePercent = Math.Round((price - previousClose) / previousClose * 100m, 4);
        }

        DateTimeOffset? asOf = null;
        if (meta.TryGetProperty("regularMarketTime", out var time)
            && time.ValueKind == JsonValueKind.Number)
            asOf = DateTimeOffset.FromUnixTimeSeconds(time.GetInt64());

        return new StockQuote(
            ticker,
            price,
            changePercent,
            meta.TryGetProperty("currency", out var currency) ? currency.GetString() : null,
            asOf ?? DateTimeOffset.UtcNow);
    }

    private async Task<string?> ResolveTickerAsync(
        string companyName,
        string? domain,
        CancellationToken cancellationToken)
    {
        var url =
            $"https://query2.finance.yahoo.com/v1/finance/search?q={Uri.EscapeDataString(companyName)}&quotesCount=6&newsCount=0";
        using var document = await GetJsonAsync(url, cancellationToken);
        if (document is null
            || !document.RootElement.TryGetProperty("quotes", out var quotes)
            || quotes.ValueKind != JsonValueKind.Array)
            return null;

        string? bestSymbol = null;
        var bestScore = 0;

        foreach (var quote in quotes.EnumerateArray())
        {
            var quoteType = quote.TryGetProperty("quoteType", out var type) ? type.GetString() : null;
            if (!string.Equals(quoteType, "EQUITY", StringComparison.OrdinalIgnoreCase))
                continue;

            var symbol = quote.TryGetProperty("symbol", out var symbolEl) ? symbolEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(symbol))
                continue;

            var longName = quote.TryGetProperty("longname", out var longEl) ? longEl.GetString() : null;
            var shortName = quote.TryGetProperty("shortname", out var shortEl) ? shortEl.GetString() : null;
            var website = quote.TryGetProperty("website", out var webEl) ? webEl.GetString() : null;

            var score = ScoreTickerMatch(companyName, domain, longName, shortName, website);
            if (score > bestScore)
            {
                bestScore = score;
                bestSymbol = symbol;
            }
        }

        return bestScore >= 3 ? bestSymbol : null;
    }

    private static int ScoreTickerMatch(
        string companyName,
        string? domain,
        string? longName,
        string? shortName,
        string? website)
    {
        var score = 0;
        var haystack = $"{longName} {shortName}";
        var companyTokens = Tokenize(companyName).ToList();
        if (companyTokens.Count == 0)
            return 0;

        var matched = companyTokens.Count(token =>
            haystack.Contains(token, StringComparison.OrdinalIgnoreCase));
        score += matched * 2;

        if (matched == companyTokens.Count)
            score += 2;

        if (haystack.Contains(companyName, StringComparison.OrdinalIgnoreCase))
            score += 2;

        if (!string.IsNullOrWhiteSpace(domain) && !string.IsNullOrWhiteSpace(website)
            && website.Contains(domain, StringComparison.OrdinalIgnoreCase))
            score += 6;

        return score;
    }

    private async Task AttachYahooThumbnailsAsync(
        IList<CompanyNewsItem> items,
        string companyName,
        CancellationToken cancellationToken)
    {
        if (items.All(n => !string.IsNullOrWhiteSpace(n.ImageUrl)))
            return;

        var url =
            $"https://query1.finance.yahoo.com/v1/finance/search?q={Uri.EscapeDataString(companyName)}&quotesCount=0&newsCount=8";
        using var document = await GetJsonAsync(url, cancellationToken);
        if (document is null
            || !document.RootElement.TryGetProperty("news", out var news)
            || news.ValueKind != JsonValueKind.Array)
            return;

        var yahooItems = new List<(string Title, string? ImageUrl)>();
        foreach (var article in news.EnumerateArray())
        {
            var title = article.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(title))
                continue;

            string? imageUrl = null;
            if (article.TryGetProperty("thumbnail", out var thumb)
                && thumb.TryGetProperty("resolutions", out var resolutions)
                && resolutions.ValueKind == JsonValueKind.Array)
            {
                var bestWidth = 0;
                foreach (var resolution in resolutions.EnumerateArray())
                {
                    var width = resolution.TryGetProperty("width", out var widthEl) && widthEl.ValueKind == JsonValueKind.Number
                        ? widthEl.GetInt32()
                        : 0;
                    var src = resolution.TryGetProperty("url", out var srcEl) ? srcEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(src) || width < bestWidth)
                        continue;
                    bestWidth = width;
                    imageUrl = src;
                }
            }

            yahooItems.Add((title, imageUrl));
        }

        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item.ImageUrl))
                continue;

            var match = yahooItems.FirstOrDefault(y => TitlesLikelyMatch(item.Title, y.Title));
            if (!string.IsNullOrWhiteSpace(match.ImageUrl))
                item.ImageUrl = match.ImageUrl;
        }
    }

    private static string? ExtractRssImageUrl(XElement item)
    {
        XNamespace media = "http://search.yahoo.com/mrss/";
        var mediaUrl = item.Descendants(media + "content").FirstOrDefault()?.Attribute("url")?.Value
            ?? item.Descendants(media + "thumbnail").FirstOrDefault()?.Attribute("url")?.Value;
        if (!string.IsNullOrWhiteSpace(mediaUrl))
            return mediaUrl.Trim();

        var enclosure = item.Element("enclosure");
        var enclosureType = enclosure?.Attribute("type")?.Value;
        var enclosureUrl = enclosure?.Attribute("url")?.Value;
        if (!string.IsNullOrWhiteSpace(enclosureUrl)
            && (string.IsNullOrWhiteSpace(enclosureType) || enclosureType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)))
            return enclosureUrl.Trim();

        return null;
    }

    private static bool TitlesLikelyMatch(string left, string right)
    {
        var a = TokenizeTitle(left).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var b = TokenizeTitle(right).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (a.Count == 0 || b.Count == 0)
            return false;

        var overlap = a.Count(token => b.Contains(token));
        return overlap >= 3 || overlap * 2 >= Math.Min(a.Count, b.Count);
    }

    private static IEnumerable<string> TokenizeTitle(string title)
    {
        var cut = title.LastIndexOf(" - ", StringComparison.Ordinal);
        if (cut > 0)
            title = title[..cut];

        return title.ToLowerInvariant()
            .Split([' ', ',', '-', '/', '|', ':', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 3);
    }

    private async Task<string?> GetStringAsync(string url, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("MarketData");
        using var response = await client.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        var body = await GetStringAsync(url, cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ExtractSourceFromTitle(string title)
    {
        var separator = title.LastIndexOf(" - ", StringComparison.Ordinal);
        return separator > 0 ? title[(separator + 3)..].Trim() : null;
    }

    private static string CleanCompanyName(string name)
    {
        name = Regex.Replace(name, @"\s[-|–].*$", string.Empty).Trim();
        name = SuffixRegex.Replace(name, string.Empty);
        return Regex.Replace(name, @"\s+", " ").Trim(' ', ',', '.', '-', '|');
    }

    private static IEnumerable<string> Tokenize(string value) =>
        SuffixRegex.Replace(value, string.Empty)
            .ToLowerInvariant()
            .Split([' ', ',', '-', '/', '&'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 2 && !StopTokens.Contains(t));

    private sealed record StockQuote(
        string Ticker,
        decimal Price,
        decimal? ChangePercent,
        string? Currency,
        DateTimeOffset AsOf);
}
