namespace LeadSphere.Discovery.Function.Options;

public sealed class SignalEvaluationOptions
{
    public const string SectionName = "SignalEvaluation";

    public bool Enabled { get; set; } = true;
    public int StaleAfterHours { get; set; } = 12;
    public int BatchSize { get; set; } = 20;
    public int MaxSearchQueries { get; set; } = 8;
    public int MaxResultsPerQuery { get; set; } = 8;
}
