namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class ApiVersionContractSources
{
    public Guid ApiVersionId { get; set; }
    public string BundleJson { get; set; } = "{}";
    public string BundleHash { get; set; } = "";
    public string SourcesJson { get; set; } = "[]";
    public string Dialect { get; set; } = "Oas30";
    public long SourcePolicyRevision { get; set; }
}
