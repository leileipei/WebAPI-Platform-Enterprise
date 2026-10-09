namespace WebApi.Contracts.Governance;
public sealed record EnvironmentAccessSettings(string? PublicOrigin,string? InternalOrigin,string BasePath);
