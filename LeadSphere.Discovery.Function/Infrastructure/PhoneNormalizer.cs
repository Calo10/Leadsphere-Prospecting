using System.Text.RegularExpressions;

namespace LeadSphere.Discovery.Function.Infrastructure;

public static class PhoneNormalizer
{
    private static readonly Regex DigitsOnly = new(@"\D", RegexOptions.Compiled);

    /// <summary>Normalizes phone numbers to E.164 (+country + number).</summary>
    public static string? Normalize(string? phone, string? locationHint = null)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return null;

        var trimmed = phone.Trim();
        var digits = DigitsOnly.Replace(trimmed, string.Empty);
        if (digits.Length < 10)
            return null;

        var defaultCountry = InferDefaultCountryCode(locationHint);

        if (trimmed.StartsWith('+'))
            return $"+{digits}";

        if (digits.Length == 10)
            return $"+{defaultCountry}{digits}";

        if (digits.Length == 11 && digits.StartsWith('1'))
            return $"+{digits}";

        if (digits.Length == 12 && digits.StartsWith("52"))
            return $"+{digits}";

        if (digits.Length > 11)
            return $"+{digits}";

        if (digits.Length > 10)
            return $"+{digits}";

        return null;
    }

    public static IEnumerable<string> NormalizeMany(IEnumerable<string> phones, string? locationHint = null)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var phone in phones)
        {
            var normalized = Normalize(phone, locationHint);
            if (normalized is not null && seen.Add(normalized))
                yield return normalized;
        }
    }

    private static string InferDefaultCountryCode(string? locationHint)
    {
        if (string.IsNullOrWhiteSpace(locationHint))
            return "1";

        var lower = locationHint.ToLowerInvariant();
        if (lower.Contains("mexico", StringComparison.Ordinal) || lower.Contains("méxico", StringComparison.Ordinal))
            return "52";

        if (lower.Contains("colombia", StringComparison.Ordinal))
            return "57";

        if (lower.Contains("argentina", StringComparison.Ordinal))
            return "54";

        if (lower.Contains("chile", StringComparison.Ordinal))
            return "56";

        if (lower.Contains("peru", StringComparison.Ordinal) || lower.Contains("perú", StringComparison.Ordinal))
            return "51";

        if (lower.Contains("spain", StringComparison.Ordinal) || lower.Contains("españa", StringComparison.Ordinal))
            return "34";

        return "1";
    }
}
