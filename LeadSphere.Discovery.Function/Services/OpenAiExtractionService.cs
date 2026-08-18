using System.Text;
using System.Text.Json;
using LeadSphere.Discovery.Function.Infrastructure;
using LeadSphere.Discovery.Function.Models;
using LeadSphere.Discovery.Function.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeadSphere.Discovery.Function.Services;

public interface IOpenAiExtractionService
{
    Task<AiExtractionResult> ExtractAsync(SearchRecord search, CompanyCandidate candidate, CancellationToken cancellationToken);
}

public sealed class OpenAiExtractionService : IOpenAiExtractionService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly OpenAiOptions _options;
    private readonly ILogger<OpenAiExtractionService> _logger;

    public OpenAiExtractionService(
        IHttpClientFactory httpClientFactory,
        IOptions<OpenAiOptions> options,
        ILogger<OpenAiExtractionService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AiExtractionResult> ExtractAsync(SearchRecord search, CompanyCandidate candidate, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new InvalidOperationException("OpenAI:ApiKey is not configured.");

        var useAzure = string.Equals(_options.Provider, "Azure", StringComparison.OrdinalIgnoreCase);

        var systemPrompt = """
            You extract structured B2B prospecting data from scraped website text for sales prospecting.
            Return ONLY valid JSON with this shape:
            {
              "company": {
                "name": "string",
                "website": "string|null",
                "domain": "string|null",
                "industry": "string|null",
                "location": "string|null",
                "description": "string|null",
                "employeeCount": number|null
              },
              "contacts": [
                {
                  "firstName": "string|null",
                  "lastName": "string|null",
                  "fullName": "string|null",
                  "email": "string|null",
                  "phone": "string|null",
                  "jobTitle": "string|null",
                  "linkedInUrl": "string|null"
                }
              ],
              "fitScore": number,
              "confidenceScore": number,
              "aiSummary": "string"
            }
            Rules:
            - fitScore must reflect how well the company matches the search industry/profile (0-1). Use scores below 0.4 for poor matches.
            - NEVER invent contacts from generic inboxes (hello@, info@, contact@, support@, sales@, admin@).
            - Only include contacts that are real named individuals with decision-maker titles (CEO, Founder, VP, Director, Head of, CRO, General Manager, etc.).
            - Each contact MUST have a full name and either a personal email, phone, or LinkedIn profile URL.
            - Prefer contacts with email and/or phone. Assign emails/phones from the scraped lists when the name clearly matches.
            - If a personal email like first.last@domain appears near a person name, attach it to that contact.
            - If a phone appears on contact/about pages, attach it to the most relevant decision-maker when reasonable.
            - linkedInUrl must be a personal profile (linkedin.com/in/username), NEVER a company page (linkedin.com/company/...).
            - Extract every named decision-maker visible on team, leadership, about, or management pages (up to 10).
            - If no real decision-makers are found, return an empty contacts array.
            - Omit fields you do not know instead of returning null or empty strings.
            - Phone numbers should be digits with optional country code when known.
            """;

        var userPrompt = new StringBuilder();
        userPrompt.AppendLine("Search profile:");
        userPrompt.AppendLine(search.ProfileDescription);
        if (search.Criteria is not null)
        {
            userPrompt.AppendLine($"Industry: {search.Criteria.Industry}");
            userPrompt.AppendLine($"Location: {search.Criteria.Location}");
            userPrompt.AppendLine($"Company size: {search.Criteria.EmployeeMin}-{search.Criteria.EmployeeMax}");
        }

        userPrompt.AppendLine();
        userPrompt.AppendLine("Scraped candidate:");
        userPrompt.AppendLine($"Name hint: {candidate.Name}");
        userPrompt.AppendLine($"Domain: {candidate.Domain}");
        userPrompt.AppendLine($"Website: {candidate.Website}");
        userPrompt.AppendLine($"Emails: {string.Join(", ", candidate.Emails)}");
        userPrompt.AppendLine($"Phones: {string.Join(", ", candidate.Phones)}");
        userPrompt.AppendLine($"People hints: {string.Join(", ", candidate.PossiblePeopleNames)}");
        userPrompt.AppendLine($"Job title hints: {string.Join(", ", candidate.JobTitles)}");
        if (candidate.LinkedInContacts.Count > 0)
        {
            userPrompt.AppendLine("LinkedIn decision-makers discovered:");
            foreach (var person in candidate.LinkedInContacts)
                userPrompt.AppendLine($"- {person.FullName} | {person.JobTitle} | {person.LinkedInUrl}");
        }
        if (candidate.SocialLinks.Count > 0)
        {
            userPrompt.AppendLine("Social / external profiles:");
            foreach (var (platform, url) in candidate.SocialLinks)
                userPrompt.AppendLine($"- {platform}: {url}");
        }
        if (candidate.Emails.Count > 0)
            userPrompt.AppendLine($"Personal emails (non-generic only): {string.Join(", ", candidate.Emails)}");
        userPrompt.AppendLine();
        userPrompt.AppendLine("Page text:");
        userPrompt.AppendLine(candidate.RawText ?? candidate.Description ?? string.Empty);

        object requestBody = useAzure
            ? new
            {
                temperature = 0.1,
                response_format = new { type = "json_object" },
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt.ToString() }
                }
            }
            : new
            {
                model = _options.Model,
                temperature = 0.1,
                response_format = new { type = "json_object" },
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt.ToString() }
                }
            };

        var client = _httpClientFactory.CreateClient("OpenAI");
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildChatCompletionsEndpoint(useAzure));
        if (useAzure)
            request.Headers.TryAddWithoutValidation("api-key", _options.ApiKey);
        else
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_options.ApiKey}");

        request.Content = new StringContent(JsonSerializer.Serialize(requestBody, JsonDefaults.Web), Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("OpenAI request failed with status {StatusCode}: {Body}", (int)response.StatusCode, errorBody);
            response.EnsureSuccessStatusCode();
        }

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var messageContent = document.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        if (string.IsNullOrWhiteSpace(messageContent))
            return new AiExtractionResult();

        var result = JsonSerializer.Deserialize<AiExtractionResult>(messageContent, JsonDefaults.Web) ?? new AiExtractionResult();

        if (result.Company is not null)
        {
            result.Company.Domain ??= candidate.Domain;
            result.Company.Website ??= candidate.Website;
            if (string.IsNullOrWhiteSpace(result.Company.Name))
                result.Company.Name = candidate.Name;
        }

        foreach (var contact in result.Contacts)
            contact.LinkedInUrl = LinkedInContactUrl.NormalizePersonal(contact.LinkedInUrl);

        return result;
    }

    private Uri BuildChatCompletionsEndpoint(bool useAzure)
    {
        if (!useAzure)
            return new Uri(new Uri(_options.BaseUrl, UriKind.Absolute), "chat/completions");

        var resource = _options.Endpoint.TrimEnd('/');
        return new Uri($"{resource}/openai/deployments/{_options.Deployment}/chat/completions?api-version={_options.ApiVersion}");
    }
}
