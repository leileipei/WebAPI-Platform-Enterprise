namespace WebApi.Contracts.OpenApi;
public sealed record ContractResourceSource(Uri Uri,string Format);
public sealed record ContractDefinitionSource(Guid Id,string Kind,Uri ResourceUri,string Pointer,string SchemaHash);
public sealed record ContractSourceMetadata(IReadOnlyList<ContractResourceSource> Documents,IReadOnlyList<ContractDefinitionSource> Definitions);
