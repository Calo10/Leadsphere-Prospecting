using Dapper;
using LeadSphere.Discovery.Function.Infrastructure;
using LeadSphere.Discovery.Function.Models;

namespace LeadSphere.Discovery.Function.Repositories;

public interface ICompanyRepository
{
    Task<bool> ExistsByDomainAsync(Guid orgId, string domain, CancellationToken cancellationToken);
    Task<Guid> InsertAsync(
        Guid orgId,
        Guid searchId,
        AiCompanyData company,
        AiExtractionResult extraction,
        CompanyEnrichmentData enrichment,
        CancellationToken cancellationToken);
}

public sealed class CompanyRepository : ICompanyRepository
{
    private readonly ISqlConnectionFactory _connectionFactory;

    public CompanyRepository(ISqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<bool> ExistsByDomainAsync(Guid orgId, string domain, CancellationToken cancellationToken)
    {
        const string sql = @"
            SELECT COUNT(1)
            FROM ls_companies
            WHERE org_id = @OrgId AND domain = @Domain;";

        await using var connection = _connectionFactory.CreateConnection();
        var command = new CommandDefinition(sql, new { OrgId = orgId, Domain = domain }, cancellationToken: cancellationToken);
        var count = await connection.ExecuteScalarAsync<int>(command);
        return count > 0;
    }

    public async Task<Guid> InsertAsync(
        Guid orgId,
        Guid searchId,
        AiCompanyData company,
        AiExtractionResult extraction,
        CompanyEnrichmentData enrichment,
        CancellationToken cancellationToken)
    {
        var row = PersistableCompanyMapper.Map(company, extraction, enrichment);

        const string sql = @"
            INSERT INTO ls_companies (
                org_id, search_id, name, domain, website, industry, employee_count, location, description,
                logo_url, linkedin_url, twitter_url, facebook_url, instagram_url, crunchbase_url,
                ticker, stock_price, stock_change_percent, stock_currency, stock_as_of,
                metadata_json)
            OUTPUT INSERTED.id
            VALUES (
                @OrgId, @SearchId, @Name, @Domain, @Website, @Industry, @EmployeeCount, @Location, @Description,
                @LogoUrl, @LinkedInUrl, @TwitterUrl, @FacebookUrl, @InstagramUrl, @CrunchbaseUrl,
                @Ticker, @StockPrice, @StockChangePercent, @StockCurrency, @StockAsOf,
                @MetadataJson);";

        await using var connection = _connectionFactory.CreateConnection();
        var command = new CommandDefinition(sql, new
        {
            OrgId = orgId,
            SearchId = searchId,
            row.Name,
            row.Domain,
            row.Website,
            row.Industry,
            row.EmployeeCount,
            row.Location,
            row.Description,
            row.LogoUrl,
            row.LinkedInUrl,
            row.TwitterUrl,
            row.FacebookUrl,
            row.InstagramUrl,
            row.CrunchbaseUrl,
            row.Ticker,
            row.StockPrice,
            row.StockChangePercent,
            row.StockCurrency,
            row.StockAsOf,
            row.MetadataJson
        }, cancellationToken: cancellationToken);

        return await connection.ExecuteScalarAsync<Guid>(command);
    }
}
