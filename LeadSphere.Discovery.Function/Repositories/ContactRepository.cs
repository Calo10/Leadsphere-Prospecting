using Dapper;
using LeadSphere.Discovery.Function.Infrastructure;
using LeadSphere.Discovery.Function.Models;

namespace LeadSphere.Discovery.Function.Repositories;

public interface IContactRepository
{
    Task<bool> ExistsByEmailAsync(Guid orgId, string email, CancellationToken cancellationToken);
    Task<bool> ExistsByNameAsync(Guid orgId, Guid companyId, string fullName, CancellationToken cancellationToken);
    Task<bool> InsertAsync(
        Guid orgId,
        Guid searchId,
        Guid companyId,
        AiContactData contact,
        EmailValidationResult? emailValidation,
        string? locationHint,
        CancellationToken cancellationToken);
}

public sealed class ContactRepository : IContactRepository
{
    private readonly ISqlConnectionFactory _connectionFactory;

    public ContactRepository(ISqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<bool> ExistsByEmailAsync(Guid orgId, string email, CancellationToken cancellationToken)
    {
        const string sql = @"
            SELECT COUNT(1)
            FROM ls_contacts
            WHERE org_id = @OrgId AND email = @Email;";

        await using var connection = _connectionFactory.CreateConnection();
        var command = new CommandDefinition(sql, new { OrgId = orgId, Email = email }, cancellationToken: cancellationToken);
        var count = await connection.ExecuteScalarAsync<int>(command);
        return count > 0;
    }

    public async Task<bool> ExistsByNameAsync(Guid orgId, Guid companyId, string fullName, CancellationToken cancellationToken)
    {
        const string sql = @"
            SELECT COUNT(1)
            FROM ls_contacts
            WHERE org_id = @OrgId
              AND company_id = @CompanyId
              AND LTRIM(RTRIM(CONCAT(first_name, ' ', last_name))) = @FullName;";

        await using var connection = _connectionFactory.CreateConnection();
        var command = new CommandDefinition(sql, new { OrgId = orgId, CompanyId = companyId, FullName = fullName }, cancellationToken: cancellationToken);
        var count = await connection.ExecuteScalarAsync<int>(command);
        return count > 0;
    }

    public async Task<bool> InsertAsync(
        Guid orgId,
        Guid searchId,
        Guid companyId,
        AiContactData contact,
        EmailValidationResult? emailValidation,
        string? locationHint,
        CancellationToken cancellationToken)
    {
        var row = PersistableContactMapper.Map(contact, locationHint);
        if (row is null)
            return false;

        const string sql = @"
            INSERT INTO ls_contacts (org_id, company_id, search_id, first_name, last_name, email, phone, job_title, linkedin_url, metadata_json)
            VALUES (@OrgId, @CompanyId, @SearchId, @FirstName, @LastName, @Email, @Phone, @JobTitle, @LinkedInUrl, @MetadataJson);";

        await using var connection = _connectionFactory.CreateConnection();
        var command = new CommandDefinition(sql, new
        {
            OrgId = orgId,
            CompanyId = companyId,
            SearchId = searchId,
            row.FirstName,
            row.LastName,
            row.Email,
            row.Phone,
            row.JobTitle,
            row.LinkedInUrl,
            MetadataJson = MetadataBuilder.ForContact(emailValidation)
        }, cancellationToken: cancellationToken);

        await connection.ExecuteAsync(command);
        return true;
    }
}
