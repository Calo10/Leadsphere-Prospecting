using Dapper;
using LeadSphere.Discovery.Function.Infrastructure;

namespace LeadSphere.Discovery.Function.Repositories;

public interface IDiscoveryJobRepository
{
    Task<bool> ExistsAsync(Guid orgId, Guid jobId, Guid searchId, CancellationToken cancellationToken);
    Task UpdateStatusAsync(Guid orgId, Guid jobId, string status, string? errorMessage, DateTimeOffset? startedAt, DateTimeOffset? completedAt, CancellationToken cancellationToken);
    Task UpdateCountersAsync(Guid orgId, Guid jobId, int companiesFound, int contactsFound, CancellationToken cancellationToken);
}

public sealed class DiscoveryJobRepository : IDiscoveryJobRepository
{
    private readonly ISqlConnectionFactory _connectionFactory;

    public DiscoveryJobRepository(ISqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<bool> ExistsAsync(Guid orgId, Guid jobId, Guid searchId, CancellationToken cancellationToken)
    {
        const string sql = @"
            SELECT COUNT(1)
            FROM ls_discovery_jobs
            WHERE org_id = @OrgId AND id = @JobId AND search_id = @SearchId;";

        await using var connection = _connectionFactory.CreateConnection();
        var command = new CommandDefinition(sql, new { OrgId = orgId, JobId = jobId, SearchId = searchId }, cancellationToken: cancellationToken);
        var count = await connection.ExecuteScalarAsync<int>(command);
        return count > 0;
    }

    public async Task UpdateStatusAsync(Guid orgId, Guid jobId, string status, string? errorMessage, DateTimeOffset? startedAt, DateTimeOffset? completedAt, CancellationToken cancellationToken)
    {
        const string sql = @"
            UPDATE ls_discovery_jobs
            SET
                status = @Status,
                error_message = @ErrorMessage,
                started_at = COALESCE(@StartedAt, started_at),
                completed_at = COALESCE(@CompletedAt, completed_at),
                updated_at = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
            WHERE org_id = @OrgId AND id = @JobId;";

        await using var connection = _connectionFactory.CreateConnection();
        var command = new CommandDefinition(sql, new
        {
            OrgId = orgId,
            JobId = jobId,
            Status = status,
            ErrorMessage = errorMessage,
            StartedAt = startedAt,
            CompletedAt = completedAt
        }, cancellationToken: cancellationToken);

        await connection.ExecuteAsync(command);
    }

    public async Task UpdateCountersAsync(Guid orgId, Guid jobId, int companiesFound, int contactsFound, CancellationToken cancellationToken)
    {
        const string sql = @"
            UPDATE ls_discovery_jobs
            SET
                companies_found_count = @CompaniesFound,
                contacts_found_count = @ContactsFound,
                updated_at = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
            WHERE org_id = @OrgId AND id = @JobId;";

        await using var connection = _connectionFactory.CreateConnection();
        var command = new CommandDefinition(sql, new
        {
            OrgId = orgId,
            JobId = jobId,
            CompaniesFound = companiesFound,
            ContactsFound = contactsFound
        }, cancellationToken: cancellationToken);

        await connection.ExecuteAsync(command);
    }
}
