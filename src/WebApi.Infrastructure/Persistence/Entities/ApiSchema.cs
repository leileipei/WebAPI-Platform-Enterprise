namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class ApiSchema
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ApiVersionId { get; set; }
    public string SchemaType { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int? StatusCode { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public string SchemaJson { get; set; } = "{}";
    public string? SchemaHash { get; set; }
    public string? ExampleJson { get; set; }
}
