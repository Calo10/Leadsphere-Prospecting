using Dapper;
using LeadSphere.Discovery.Function.Constants;
using LeadSphere.Discovery.Function.Infrastructure;
using LeadSphere.Discovery.Function.Models;

namespace LeadSphere.Discovery.Function.Repositories;

public interface ISignalRepository
{
    Task<IReadOnlyList<SignalDueJob>> ListDueForEvaluationAsync(DateTimeOffset now, DateTimeOffset staleBefore, int take, CancellationToken cancellationToken);
    Task<SignalDueJob?> GetJobAsync(Guid orgId, Guid signalId, CancellationToken cancellationToken);
    Task<SignalSnapshotRecord?> GetLatestSnapshotAsync(Guid signalId, CancellationToken cancellationToken);
    Task<Guid> InsertSnapshotAsync(Guid signalId, SignalSnapshotRecord snapshot, CancellationToken cancellationToken);
    Task InsertEventsAsync(Guid signalId, Guid? snapshotId, IReadOnlyList<SignalEventDraft> events, DateTimeOffset eventDate, CancellationToken cancellationToken);
    Task MarkEvaluatedAsync(Guid orgId, Guid id, string status, DateTimeOffset evaluatedAt, CancellationToken cancellationToken);
    Task<SignalSnapshotPayload?> CollectCompanySnapshotAsync(Guid orgId, Guid companyId, CancellationToken cancellationToken);
}

public sealed class SignalRepository : ISignalRepository
{
    private readonly ISqlConnectionFactory _connectionFactory;

    public SignalRepository(ISqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<SignalDueJob>> ListDueForEvaluationAsync(
        DateTimeOffset now,
        DateTimeOffset staleBefore,
        int take,
        CancellationToken cancellationToken)
    {
        const string sql = @"
            SELECT TOP (@Take)
                id AS Id,
                org_id AS OrgId,
                company_id AS CompanyId,
                status AS Status,
                end_date AS EndDate
            FROM ls_signals
            WHERE status IN (@Active, @Collecting)
              AND (end_date <= @Now OR last_evaluation_date IS NULL OR last_evaluation_date <= @StaleBefore)
            ORDER BY ISNULL(last_evaluation_date, start_date) ASC;";

        await using var connection = _connectionFactory.CreateConnection();
        var command = new CommandDefinition(sql, new
        {
            Take = take,
            Active = SignalStatuses.Active,
            Collecting = SignalStatuses.Collecting,
            Now = now,
            StaleBefore = staleBefore
        }, cancellationToken: cancellationToken);
        var rows = await connection.QueryAsync<SignalDueJob>(command);
        return rows.ToList();
    }

    public async Task<SignalDueJob?> GetJobAsync(Guid orgId, Guid signalId, CancellationToken cancellationToken)
    {
        const string sql = @"
            SELECT
                id AS Id,
                org_id AS OrgId,
                company_id AS CompanyId,
                status AS Status,
                end_date AS EndDate
            FROM ls_signals
            WHERE org_id = @OrgId AND id = @SignalId;";

        await using var connection = _connectionFactory.CreateConnection();
        var command = new CommandDefinition(sql, new { OrgId = orgId, SignalId = signalId }, cancellationToken: cancellationToken);
        return await connection.QueryFirstOrDefaultAsync<SignalDueJob>(command);
    }

    public async Task<SignalSnapshotRecord?> GetLatestSnapshotAsync(Guid signalId, CancellationToken cancellationToken)
    {
        const string sql = @"
            SELECT TOP 1
                id AS Id,
                signal_id AS SignalId,
                snapshot_date AS SnapshotDate,
                company_name AS CompanyName,
                employee_count AS EmployeeCount,
                contact_count AS ContactCount,
                news_count AS NewsCount,
                industry AS Industry,
                description AS Description,
                website AS Website,
                location AS Location,
                raw_json AS RawJson
            FROM ls_signal_snapshots
            WHERE signal_id = @SignalId
            ORDER BY snapshot_date DESC, created_at DESC;";

        await using var connection = _connectionFactory.CreateConnection();
        var command = new CommandDefinition(sql, new { SignalId = signalId }, cancellationToken: cancellationToken);
        return await connection.QueryFirstOrDefaultAsync<SignalSnapshotRecord>(command);
    }

    public async Task<Guid> InsertSnapshotAsync(Guid signalId, SignalSnapshotRecord snapshot, CancellationToken cancellationToken)
    {
        var id = snapshot.Id == Guid.Empty ? Guid.NewGuid() : snapshot.Id;
        const string sql = @"
            INSERT INTO ls_signal_snapshots (
                id, signal_id, snapshot_date, company_name, employee_count, contact_count, news_count,
                industry, description, website, location, raw_json, created_at
            )
            VALUES (
                @Id, @SignalId, @SnapshotDate, @CompanyName, @EmployeeCount, @ContactCount, @NewsCount,
                @Industry, @Description, @Website, @Location, @RawJson,
                TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
            );";

        await using var connection = _connectionFactory.CreateConnection();
        var command = new CommandDefinition(sql, new
        {
            Id = id,
            SignalId = signalId,
            snapshot.SnapshotDate,
            snapshot.CompanyName,
            snapshot.EmployeeCount,
            snapshot.ContactCount,
            snapshot.NewsCount,
            snapshot.Industry,
            snapshot.Description,
            snapshot.Website,
            snapshot.Location,
            snapshot.RawJson
        }, cancellationToken: cancellationToken);
        await connection.ExecuteAsync(command);
        return id;
    }

    public async Task InsertEventsAsync(
        Guid signalId,
        Guid? snapshotId,
        IReadOnlyList<SignalEventDraft> events,
        DateTimeOffset eventDate,
        CancellationToken cancellationToken)
    {
        if (events.Count == 0)
            return;

        const string sql = @"
            INSERT INTO ls_signal_events (
                id, signal_id, snapshot_id, event_type, severity, title, description,
                previous_value, new_value, event_date, created_at
            )
            VALUES (
                @Id, @SignalId, @SnapshotId, @EventType, @Severity, @Title, @Description,
                @PreviousValue, @NewValue, @EventDate,
                TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
            );";

        await using var connection = _connectionFactory.CreateConnection();
        foreach (var draft in events)
        {
            var command = new CommandDefinition(sql, new
            {
                Id = Guid.NewGuid(),
                SignalId = signalId,
                SnapshotId = snapshotId,
                draft.EventType,
                draft.Severity,
                draft.Title,
                draft.Description,
                draft.PreviousValue,
                draft.NewValue,
                EventDate = eventDate
            }, cancellationToken: cancellationToken);
            await connection.ExecuteAsync(command);
        }
    }

    public async Task MarkEvaluatedAsync(Guid orgId, Guid id, string status, DateTimeOffset evaluatedAt, CancellationToken cancellationToken)
    {
        const string sql = @"
            UPDATE ls_signals
            SET status = @Status,
                last_evaluation_date = @EvaluatedAt,
                updated_at = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
            WHERE org_id = @OrgId AND id = @Id;";

        await using var connection = _connectionFactory.CreateConnection();
        var command = new CommandDefinition(sql, new
        {
            OrgId = orgId,
            Id = id,
            Status = status,
            EvaluatedAt = evaluatedAt
        }, cancellationToken: cancellationToken);
        await connection.ExecuteAsync(command);
    }

    public async Task<SignalSnapshotPayload?> CollectCompanySnapshotAsync(Guid orgId, Guid companyId, CancellationToken cancellationToken)
    {
        const string companySql = @"
            SELECT
                name AS Name,
                description AS Description,
                employee_count AS EmployeeCount,
                industry AS Industry,
                website AS Website,
                domain AS Domain,
                location AS Location,
                linkedin_url AS LinkedInUrl,
                twitter_url AS TwitterUrl,
                facebook_url AS FacebookUrl,
                instagram_url AS InstagramUrl,
                crunchbase_url AS CrunchbaseUrl,
                metadata_json AS MetadataJson
            FROM ls_companies
            WHERE org_id = @OrgId AND id = @CompanyId;";

        const string contactsSql = @"
            SELECT
                first_name AS FirstName,
                last_name AS LastName,
                job_title AS JobTitle,
                linkedin_url AS LinkedInUrl
            FROM ls_contacts
            WHERE org_id = @OrgId AND company_id = @CompanyId;";

        await using var connection = _connectionFactory.CreateConnection();
        var company = await connection.QueryFirstOrDefaultAsync<CompanySnapshotRow>(
            new CommandDefinition(companySql, new { OrgId = orgId, CompanyId = companyId }, cancellationToken: cancellationToken));
        if (company is null)
            return null;

        var contacts = (await connection.QueryAsync<ContactSnapshotRow>(
            new CommandDefinition(contactsSql, new { OrgId = orgId, CompanyId = companyId }, cancellationToken: cancellationToken))).ToList();

        var social = new[]
            {
                company.LinkedInUrl,
                company.TwitterUrl,
                company.FacebookUrl,
                company.InstagramUrl,
                company.CrunchbaseUrl
            }
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new SignalSnapshotPayload
        {
            CompanyName = company.Name,
            Description = company.Description,
            EmployeeCount = company.EmployeeCount,
            Industry = company.Industry,
            Website = company.Website ?? company.Domain,
            Location = company.Location,
            ContactCount = contacts.Count,
            NewsCount = ParseNews(company.MetadataJson).Count,
            NewsItems = ParseNews(company.MetadataJson),
            SocialLinks = social,
            Technologies = Array.Empty<string>(),
            Contacts = contacts.Select(c => new SignalSnapshotContact
            {
                FullName = string.Join(' ', new[] { c.FirstName, c.LastName }.Where(p => !string.IsNullOrWhiteSpace(p))),
                JobTitle = c.JobTitle,
                LinkedInUrl = c.LinkedInUrl
            }).ToList()
        };
    }

    private static List<SignalSnapshotNewsItem> ParseNews(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
            return [];

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(metadataJson);
            if (!doc.RootElement.TryGetProperty("news", out var news) || news.ValueKind != System.Text.Json.JsonValueKind.Array)
                return [];

            var items = new List<SignalSnapshotNewsItem>();
            foreach (var node in news.EnumerateArray().Take(8))
            {
                var title = node.TryGetProperty("title", out var titleNode) ? titleNode.GetString() : null;
                if (string.IsNullOrWhiteSpace(title))
                    continue;

                items.Add(new SignalSnapshotNewsItem
                {
                    Title = title,
                    Url = node.TryGetProperty("url", out var urlNode) ? urlNode.GetString() : null,
                    Source = node.TryGetProperty("source", out var sourceNode) ? sourceNode.GetString() : null,
                    PublishedAt = node.TryGetProperty("publishedAt", out var dateNode) && dateNode.ValueKind == System.Text.Json.JsonValueKind.String
                        && DateTimeOffset.TryParse(dateNode.GetString(), out var publishedAt)
                        ? publishedAt
                        : null
                });
            }

            return items;
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }
}
