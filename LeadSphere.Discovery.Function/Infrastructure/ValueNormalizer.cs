using System.Text.Json;
using System.Text.Json.Serialization;
using LeadSphere.Discovery.Function.Models;

namespace LeadSphere.Discovery.Function.Infrastructure;

internal static class ValueNormalizer
{
    public static string? Text(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    public static string? Url(string? value)
    {
        var text = Text(value);
        if (text is null)
            return null;

        return Uri.TryCreate(text, UriKind.Absolute, out _) ? text : null;
    }

    public static string? SerializeMetadata(object? payload)
    {
        if (payload is null)
            return null;

        var json = JsonSerializer.Serialize(payload, MetadataJsonOptions);
        return json is "{}" or "[]" ? null : json;
    }

    private static readonly JsonSerializerOptions MetadataJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

internal static class MetadataBuilder
{
    public static string? ForCompany(AiExtractionResult extraction, CompanyEnrichmentData enrichment)
    {
        var payload = new Dictionary<string, object?>();

        if (extraction.FitScore.HasValue)
            payload["fitScore"] = extraction.FitScore.Value;
        if (extraction.ConfidenceScore.HasValue)
            payload["confidenceScore"] = extraction.ConfidenceScore.Value;

        var summary = ValueNormalizer.Text(extraction.AiSummary);
        if (summary is not null)
            payload["aiSummary"] = summary;

        if (enrichment.EnrichmentSources.Count > 0)
            payload["enrichmentSources"] = enrichment.EnrichmentSources;

        if (enrichment.EmailValidations.Count > 0)
            payload["emailValidations"] = enrichment.EmailValidations;

        if (!string.IsNullOrWhiteSpace(enrichment.Ticker))
        {
            payload["stock"] = new
            {
                ticker = enrichment.Ticker,
                price = enrichment.StockPrice,
                changePercent = enrichment.StockChangePercent,
                currency = enrichment.StockCurrency,
                asOf = enrichment.StockAsOf
            };
        }

        if (enrichment.News.Count > 0)
            payload["news"] = enrichment.News;

        return payload.Count == 0 ? null : ValueNormalizer.SerializeMetadata(payload);
    }

    public static string? ForContact(EmailValidationResult? emailValidation)
    {
        if (emailValidation is null)
            return null;

        return ValueNormalizer.SerializeMetadata(new
        {
            emailValidation = new
            {
                status = emailValidation.Status,
                isValidFormat = emailValidation.IsValidFormat,
                hasMxRecord = emailValidation.HasMxRecord
            }
        });
    }
}
