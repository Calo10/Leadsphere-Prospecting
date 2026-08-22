using System.Text.Json;
using LeadSphere.Discovery.Function.Constants;
using LeadSphere.Discovery.Function.Models;
using LeadSphere.Discovery.Function.Options;
using LeadSphere.Discovery.Function.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeadSphere.Discovery.Function.Services;

public interface ISignalEvaluationService
{
    Task EvaluateDueAsync(CancellationToken cancellationToken);
    Task EvaluateOneAsync(Guid orgId, Guid signalId, bool ignoreSilence, CancellationToken cancellationToken);
}

public sealed class SignalEvaluationService : ISignalEvaluationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly ISignalRepository _signals;
    private readonly ISignalIntelligenceCollector _intelligence;
    private readonly SignalEvaluationOptions _options;
    private readonly ILogger<SignalEvaluationService> _logger;

    public SignalEvaluationService(
        ISignalRepository signals,
        ISignalIntelligenceCollector intelligence,
        IOptions<SignalEvaluationOptions> options,
        ILogger<SignalEvaluationService> logger)
    {
        _signals = signals;
        _intelligence = intelligence;
        _options = options.Value;
        _logger = logger;
    }

    public async Task EvaluateDueAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Signal evaluation is disabled.");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var staleBefore = now.AddHours(-Math.Max(1, _options.StaleAfterHours));
        var due = await _signals.ListDueForEvaluationAsync(now, staleBefore, Math.Clamp(_options.BatchSize, 1, 100), cancellationToken);
        if (due.Count == 0)
            return;

        _logger.LogInformation("Evaluating {Count} due company signals.", due.Count);
        foreach (var job in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await EvaluateJobAsync(job, now, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to evaluate signal {SignalId}", job.Id);
            }
        }
    }

    public async Task EvaluateOneAsync(Guid orgId, Guid signalId, bool ignoreSilence, CancellationToken cancellationToken)
    {
        var job = await _signals.GetJobAsync(orgId, signalId, cancellationToken);
        if (job is null)
        {
            _logger.LogWarning("Signal {SignalId} not found for evaluation.", signalId);
            return;
        }

        if (string.Equals(job.Status, SignalStatuses.Cancelled, StringComparison.OrdinalIgnoreCase))
            return;

        if (string.Equals(job.Status, SignalStatuses.Silenced, StringComparison.OrdinalIgnoreCase) && !ignoreSilence)
        {
            _logger.LogInformation("Skipping silenced signal {SignalId}", signalId);
            return;
        }

        await EvaluateJobAsync(job, DateTimeOffset.UtcNow, cancellationToken);
    }

    private async Task EvaluateJobAsync(SignalDueJob job, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (now >= job.EndDate)
        {
            await _signals.InsertEventsAsync(job.Id, null, new[]
            {
                new SignalEventDraft
                {
                    EventType = SignalEventTypes.SignalExpired,
                    Severity = SignalSeverities.Info,
                    Title = "Signal Expired",
                    Description = "Monitoring period ended."
                }
            }, now, cancellationToken);
            await _signals.MarkEvaluatedAsync(job.OrgId, job.Id, SignalStatuses.Expired, now, cancellationToken);
            return;
        }

        var current = await _signals.CollectCompanySnapshotAsync(job.OrgId, job.CompanyId, cancellationToken);
        if (current is null)
        {
            _logger.LogWarning("Company {CompanyId} missing for signal {SignalId}", job.CompanyId, job.Id);
            return;
        }

        var liveNews = await _intelligence.CollectAsync(
            current.CompanyName ?? string.Empty,
            current.Location,
            cancellationToken);
        current.NewsItems = MergeNews(liveNews, current.NewsItems);
        current.NewsCount = current.NewsItems.Count;

        var previousRow = await _signals.GetLatestSnapshotAsync(job.Id, cancellationToken);
        var previous = Deserialize(previousRow);
        var snapshotId = await StoreSnapshotAsync(job.Id, current, now, cancellationToken);

        var drafts = new List<SignalEventDraft>
        {
            new()
            {
                EventType = SignalEventTypes.SnapshotCreated,
                Severity = SignalSeverities.Info,
                Title = "Snapshot Created",
                Description = "Periodic snapshot captured for monitoring."
            }
        };
        drafts.AddRange(SignalChangeDetector.Detect(previous, current));

        await _signals.InsertEventsAsync(job.Id, snapshotId, drafts, now, cancellationToken);
        await _signals.MarkEvaluatedAsync(job.OrgId, job.Id, SignalStatuses.Active, now, cancellationToken);
    }

    private async Task<Guid> StoreSnapshotAsync(Guid signalId, SignalSnapshotPayload payload, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var snapshot = new SignalSnapshotRecord
        {
            Id = Guid.NewGuid(),
            SignalId = signalId,
            SnapshotDate = now,
            CompanyName = payload.CompanyName,
            EmployeeCount = payload.EmployeeCount,
            ContactCount = payload.ContactCount,
            NewsCount = payload.NewsCount,
            Industry = payload.Industry,
            Description = payload.Description,
            Website = payload.Website,
            Location = payload.Location,
            RawJson = JsonSerializer.Serialize(payload, JsonOptions)
        };
        return await _signals.InsertSnapshotAsync(signalId, snapshot, cancellationToken);
    }

    private static SignalSnapshotPayload? Deserialize(SignalSnapshotRecord? snapshot)
    {
        if (snapshot is null)
            return null;

        if (!string.IsNullOrWhiteSpace(snapshot.RawJson))
        {
            try
            {
                return JsonSerializer.Deserialize<SignalSnapshotPayload>(snapshot.RawJson, JsonOptions);
            }
            catch (JsonException)
            {
                // Fall back to column values.
            }
        }

        return new SignalSnapshotPayload
        {
            CompanyName = snapshot.CompanyName,
            Description = snapshot.Description,
            EmployeeCount = snapshot.EmployeeCount,
            Industry = snapshot.Industry,
            Website = snapshot.Website,
            Location = snapshot.Location,
            ContactCount = snapshot.ContactCount ?? 0,
            NewsCount = snapshot.NewsCount ?? 0
        };
    }

    private static IReadOnlyList<SignalSnapshotNewsItem> MergeNews(
        IReadOnlyList<SignalSnapshotNewsItem> live,
        IReadOnlyList<SignalSnapshotNewsItem> stored)
    {
        var items = live.ToList();
        var seen = new HashSet<string>(items.Select(i => i.Title), StringComparer.OrdinalIgnoreCase);
        foreach (var item in stored)
        {
            if (string.IsNullOrWhiteSpace(item.Title) || !seen.Add(item.Title))
                continue;
            items.Add(item);
        }

        return items.Take(12).ToList();
    }
}
