using System.Text.Json;

namespace WebApi.Domain.Policies;

public sealed class CompiledJsonBodyMapping
{
    internal IReadOnlyList<CompiledJsonBodyOperation> Operations { get; }
    public int OperationCount => Operations.Count;
    internal CompiledJsonBodyMapping(IReadOnlyList<CompiledJsonBodyOperation> operations)
    { Operations = Array.AsReadOnly(operations.ToArray()); }
}
internal sealed record CompiledJsonBodyOperation(string Op, IReadOnlyList<string> Path,
    IReadOnlyList<string>? From, JsonElement? Value);
public sealed record JsonBodyMappingResult(byte[]? Utf8, string? Reason, int? OperationIndex);
