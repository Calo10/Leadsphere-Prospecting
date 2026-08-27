using LeadSphere.Discovery.Function.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace LeadSphere.Discovery.Function.Functions;

public sealed class SignalEvaluationFunction
{
    private readonly ISignalEvaluationService _evaluation;
    private readonly ILogger<SignalEvaluationFunction> _logger;

    public SignalEvaluationFunction(
        ISignalEvaluationService evaluation,
        ILogger<SignalEvaluationFunction> logger)
    {
        _evaluation = evaluation;
        _logger = logger;
    }

    [Function(nameof(SignalEvaluationFunction))]
    public async Task Run(
        [TimerTrigger("0 0 0,12 * * *")] TimerInfo timer,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Signal evaluation timer fired. next={Next}",
            timer.ScheduleStatus?.Next);

        await _evaluation.EvaluateDueAsync(cancellationToken);
    }
}
