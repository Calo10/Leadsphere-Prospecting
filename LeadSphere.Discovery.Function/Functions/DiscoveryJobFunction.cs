using System.Text.Json;
using Azure.Messaging.ServiceBus;
using LeadSphere.Discovery.Function.Models;
using LeadSphere.Discovery.Function.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace LeadSphere.Discovery.Function.Functions;

public sealed class DiscoveryJobFunction
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDiscoveryService _discoveryService;
    private readonly ILogger<DiscoveryJobFunction> _logger;

    public DiscoveryJobFunction(IDiscoveryService discoveryService, ILogger<DiscoveryJobFunction> logger)
    {
        _discoveryService = discoveryService;
        _logger = logger;
    }

    [Function(nameof(DiscoveryJobFunction))]
    public async Task Run(
        [ServiceBusTrigger("%DiscoveryJobsQueueName%", Connection = "ServiceBusConnection")] ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,
        CancellationToken cancellationToken)
    {
        DiscoveryJobMessage? payload;
        try
        {
            payload = JsonSerializer.Deserialize<DiscoveryJobMessage>(message.Body.ToString(), JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid JSON on discovery queue message. messageId={MessageId}", message.MessageId);
            await messageActions.CompleteMessageAsync(message, cancellationToken);
            return;
        }

        if (payload is null || payload.JobId == Guid.Empty || payload.SearchId == Guid.Empty || payload.OrgId == Guid.Empty)
        {
            _logger.LogWarning("Invalid discovery job payload. messageId={MessageId}", message.MessageId);
            await messageActions.CompleteMessageAsync(message, cancellationToken);
            return;
        }

        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["jobId"] = payload.JobId,
            ["searchId"] = payload.SearchId,
            ["orgId"] = payload.OrgId,
            ["messageId"] = message.MessageId
        });

        _logger.LogInformation(
            "Processing discovery job. deliveryCount={DeliveryCount}",
            message.DeliveryCount);

        try
        {
            await _discoveryService.ProcessJobAsync(payload, cancellationToken);
            await messageActions.CompleteMessageAsync(message, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Discovery job processing failed; message will be retried or dead-lettered.");
            throw;
        }
    }
}
