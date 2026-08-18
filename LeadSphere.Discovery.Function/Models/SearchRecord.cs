using System.Text.Json;

namespace LeadSphere.Discovery.Function.Models;

public sealed class SearchRecord
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ProfileDescription { get; set; } = string.Empty;
    public string? CriteriaJson { get; set; }
    public string Status { get; set; } = string.Empty;

    public SearchCriteria? Criteria => string.IsNullOrWhiteSpace(CriteriaJson)
        ? null
        : JsonSerializer.Deserialize<SearchCriteria>(CriteriaJson, JsonDefaults.Web);
}

public sealed class SearchCriteria
{
    public string? Location { get; set; }
    public string? Industry { get; set; }
    public int? EmployeeMin { get; set; }
    public int? EmployeeMax { get; set; }
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
}
