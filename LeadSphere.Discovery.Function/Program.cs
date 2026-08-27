using LeadSphere.Discovery.Function.Infrastructure;
using LeadSphere.Discovery.Function.Options;
using LeadSphere.Discovery.Function.Repositories;
using LeadSphere.Discovery.Function.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        services.Configure<DiscoveryOptions>(context.Configuration.GetSection(DiscoveryOptions.SectionName));
        services.Configure<WebSearchOptions>(context.Configuration.GetSection(WebSearchOptions.SectionName));
        services.Configure<OpenAiOptions>(context.Configuration.GetSection(OpenAiOptions.SectionName));
        services.Configure<SignalEvaluationOptions>(context.Configuration.GetSection(SignalEvaluationOptions.SectionName));

        services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();

        services.AddScoped<ISearchRepository, SearchRepository>();
        services.AddScoped<IDiscoveryJobRepository, DiscoveryJobRepository>();
        services.AddScoped<ICompanyRepository, CompanyRepository>();
        services.AddScoped<IContactRepository, ContactRepository>();
        services.AddScoped<ISignalRepository, SignalRepository>();
        services.AddScoped<ISignalIntelligenceCollector, SignalIntelligenceCollector>();
        services.AddScoped<ISignalEvaluationService, SignalEvaluationService>();

        services.AddScoped<IWebSearchService, WebSearchService>();
        services.AddScoped<IWebScraperService, WebScraperService>();
        services.AddScoped<ICompanyEnrichmentService, CompanyEnrichmentService>();
        services.AddScoped<ICompanyMarketDataService, CompanyMarketDataService>();
        services.AddScoped<ILinkedInPeopleDiscoveryService, LinkedInPeopleDiscoveryService>();
        services.AddScoped<IContactLinkedInDiscoveryService, ContactLinkedInDiscoveryService>();
        services.AddScoped<IContactDataEnrichmentService, ContactDataEnrichmentService>();
        services.AddScoped<ILogoResolutionService, LogoResolutionService>();
        services.AddScoped<IEmailValidationService, EmailValidationService>();
        services.AddScoped<IOpenAiExtractionService, OpenAiExtractionService>();
        services.AddScoped<IDiscoveryService, DiscoveryService>();

        services.AddHttpClient("WebSearch", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddHttpClient("WebScraper", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(20);
        });

        services.AddHttpClient("MarketData", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (compatible; LeadSphereDiscovery/1.0)");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, application/rss+xml, application/xml, text/xml");
        });

        services.AddHttpClient("OpenAI", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(90);
        });
    })
    .Build();

host.Run();
