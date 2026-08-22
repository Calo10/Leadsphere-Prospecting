namespace LeadSphere.Discovery.Function.Models;

public sealed class SignalJobMessage
{
    public Guid SignalId { get; set; }
    public Guid OrgId { get; set; }
    public bool IgnoreSilence { get; set; }
}
