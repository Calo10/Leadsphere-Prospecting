using System.Text.Json;
using Azure.Messaging.ServiceBus;
using LeadSphere.Discovery.Function.Models;
using LeadSphere.Discovery.Function.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace LeadSphere.Discovery.Function.Functions;

public sealed class SignalJobFunction
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ISignalEvaluationService _evaluation;
    private readonly ILogger<SignalJobFunction> _logger;

    public SignalJobFunction(ISignalEvaluationService evaluation, ILogger<SignalJobFunction> logger)
    {
        _evaluation = evaluation;
        _logger = logger;
    }

    [Function(nameof(SignalJobFunction))]
    public async Task Run(
        [ServiceBusTrigger("%SignalJobsQueueName%", Connection = "ServiceBusConnection")] ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,
        CancellationToken cancellationToken)
    {
        SignalJobMessage? payload;
        try
        {
            payload = JsonSerializer.Deserialize<SignalJobMessage>(message.Body.ToString(), JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid JSON on signal queue message. messageId={MessageId}", message.MessageId);
            await messageActions.CompleteMessageAsync(message, cancellationToken);
            return;
        }

        if (payload is null || payload.SignalId == Guid.Empty || payload.OrgId == Guid.Empty)
        {
            _logger.LogWarning("Invalid signal job payload. messageId={MessageId}", message.MessageId);
            await messageActions.CompleteMessageAsync(message, cancellationToken);
            return;
        }

        _logger.LogInformation("Evaluating signal {SignalId} from queue.", payload.SignalId);

        try
        {
            await _evaluation.EvaluateOneAsync(payload.OrgId, payload.SignalId, payload.IgnoreSilence, cancellationToken);
            await messageActions.CompleteMessageAsync(message, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Signal job failed for {SignalId}; message will be retried or dead-lettered.", payload.SignalId);
            throw;
        }
    }
}
