using System.Text.Json.Nodes;
namespace WebApi.Contracts.OpenApi;
public enum ContractDialect { Oas30, Oas31 }
public sealed record ContractSource(Uri LogicalUri, string RawText, string Format);
public sealed record ContractIssue(string Code, string Pointer, string Message, int? Line = null, int? Column = null);
public sealed record ContractDocument(ContractSource Source, JsonNode Root, ContractDialect Dialect,
    string CanonicalJson, IReadOnlyDictionary<string, ContractIssue> Locations);
public sealed record ContractBundle(Uri RootUri, IReadOnlyList<ContractDocument> Documents, string Hash);
