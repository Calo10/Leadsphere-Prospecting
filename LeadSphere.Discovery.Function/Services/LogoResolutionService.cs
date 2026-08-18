using LeadSphere.Discovery.Function.Models;
using Microsoft.Extensions.Logging;

namespace LeadSphere.Discovery.Function.Services;

public interface ILogoResolutionService
{
    Task<string?> ResolveAsync(CompanyCandidate candidate, CancellationToken cancellationToken);
}

public sealed class LogoResolutionService : ILogoResolutionService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LogoResolutionService> _logger;

    public LogoResolutionService(IHttpClientFactory httpClientFactory, ILogger<LogoResolutionService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<string?> ResolveAsync(CompanyCandidate candidate, CancellationToken cancellationToken)
    {
        foreach (var candidateUrl in candidate.LogoCandidateUrls)
        {
            if (await IsReachableImageAsync(candidateUrl, cancellationToken))
                return candidateUrl;
        }

        if (!string.IsNullOrWhiteSpace(candidate.Domain))
        {
            var clearbit = $"https://logo.clearbit.com/{candidate.Domain}";
            if (await IsReachableImageAsync(clearbit, cancellationToken))
                return clearbit;

            return $"https://www.google.com/s2/favicons?domain={Uri.EscapeDataString(candidate.Domain)}&sz=128";
        }

        return null;
    }

    private async Task<bool> IsReachableImageAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("WebScraper");
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return false;

            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            return contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                   || url.Contains("favicons", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Logo URL not reachable: {Url}", url);
            return false;
        }
    }
}
