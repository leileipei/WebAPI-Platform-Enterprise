namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class ApiParameter
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ApiVersionId { get; set; }
    public string Location { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public bool Required { get; set; }
    public string? Schema { get; set; }
    public string? Description { get; set; }
    public string? ExampleJson { get; set; }
}
