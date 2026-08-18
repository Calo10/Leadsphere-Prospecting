using Dapper;
using LeadSphere.Discovery.Function.Constants;
using LeadSphere.Discovery.Function.Infrastructure;
using LeadSphere.Discovery.Function.Models;

namespace LeadSphere.Discovery.Function.Repositories;

public interface ISearchRepository
{
    Task<SearchRecord?> GetByIdAsync(Guid orgId, Guid searchId, CancellationToken cancellationToken);
    Task UpdateStatusAsync(Guid orgId, Guid searchId, string status, string? errorMessage, DateTimeOffset? startedAt, DateTimeOffset? completedAt, CancellationToken cancellationToken);
    Task UpdateCountersAsync(Guid orgId, Guid searchId, int companiesFound, int contactsFound, CancellationToken cancellationToken);
}

public sealed class SearchRepository : ISearchRepository
{
    private readonly ISqlConnectionFactory _connectionFactory;

    public SearchRepository(ISqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<SearchRecord?> GetByIdAsync(Guid orgId, Guid searchId, CancellationToken cancellationToken)
    {
        const string sql = @"
            SELECT
                id AS Id,
                org_id AS OrgId,
                name AS Name,
                profile_description AS ProfileDescription,
                criteria_json AS CriteriaJson,
                status AS Status
            FROM ls_searches
            WHERE org_id = @OrgId AND id = @SearchId;";

        await using var connection = _connectionFactory.CreateConnection();
        var command = new CommandDefinition(sql, new { OrgId = orgId, SearchId = searchId }, cancellationToken: cancellationToken);
        return await connection.QueryFirstOrDefaultAsync<SearchRecord>(command);
    }

    public async Task UpdateStatusAsync(Guid orgId, Guid searchId, string status, string? errorMessage, DateTimeOffset? startedAt, DateTimeOffset? completedAt, CancellationToken cancellationToken)
    {
        const string sql = @"
            UPDATE ls_searches
            SET
                status = @Status,
                error_message = @ErrorMessage,
                started_at = COALESCE(@StartedAt, started_at),
                completed_at = COALESCE(@CompletedAt, completed_at),
                updated_at = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
            WHERE org_id = @OrgId AND id = @SearchId;";

        await using var connection = _connectionFactory.CreateConnection();
        var command = new CommandDefinition(sql, new
        {
            OrgId = orgId,
            SearchId = searchId,
            Status = status,
            ErrorMessage = errorMessage,
            StartedAt = startedAt,
            CompletedAt = completedAt
        }, cancellationToken: cancellationToken);

        await connection.ExecuteAsync(command);
    }

    public async Task UpdateCountersAsync(Guid orgId, Guid searchId, int companiesFound, int contactsFound, CancellationToken cancellationToken)
    {
        // Counters are derived from related rows in the API; discovery_jobs stores job-level counts.
        // This method is a no-op placeholder for future denormalized columns on ls_searches.
        await Task.CompletedTask;
    }
}
