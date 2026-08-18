namespace LeadSphere.Discovery.Function.Models;

public sealed class DiscoveryJobMessage
{
    public Guid JobId { get; set; }
    public Guid SearchId { get; set; }
    public Guid OrgId { get; set; }
}
