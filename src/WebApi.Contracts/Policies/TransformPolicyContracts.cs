using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebApi.Contracts.Policies;

[JsonConverter(typeof(JsonStringEnumConverter<TransformAction>))]
public enum TransformAction { Set, Remove }

public sealed record HeaderTransformOperation(TransformAction Action, string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Value);
public sealed record QueryTransformOperation(TransformAction Action, string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Value);
public sealed record PathTransformConfiguration(string? RemovePrefix, string? AddPrefix);

public sealed record JsonBodyOperation
{
    public string Op { get; }
    public string Path { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? From { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public JsonElement? Value { get; }
    public JsonBodyOperation(string op, string path, string? from, JsonElement? value)
    { Op = op; Path = path; From = from; Value = value?.Clone(); }
}

public sealed record RequestJsonBodyConfiguration
{
    public IReadOnlyList<string> Methods { get; }
    public int MaxBodyBytes { get; }
    public IReadOnlyList<JsonBodyOperation> Operations { get; }
    public RequestJsonBodyConfiguration(IReadOnlyList<string> methods, int maxBodyBytes, IReadOnlyList<JsonBodyOperation> operations)
    { Methods = Array.AsReadOnly(methods.ToArray()); MaxBodyBytes = maxBodyBytes; Operations = Array.AsReadOnly(operations.ToArray()); }
}
public sealed record ResponseJsonBodyConfiguration
{
    public IReadOnlyList<int> StatusCodes { get; }
    public int MaxBodyBytes { get; }
    public IReadOnlyList<JsonBodyOperation> Operations { get; }
    public ResponseJsonBodyConfiguration(IReadOnlyList<int> statusCodes, int maxBodyBytes, IReadOnlyList<JsonBodyOperation> operations)
    { StatusCodes = Array.AsReadOnly(statusCodes.ToArray()); MaxBodyBytes = maxBodyBytes; Operations = Array.AsReadOnly(operations.ToArray()); }
}
public sealed record TransformConfiguration
{
    public IReadOnlyList<HeaderTransformOperation> RequestHeaders { get; }
    public IReadOnlyList<HeaderTransformOperation> ResponseHeaders { get; }
    public PathTransformConfiguration? Path { get; }
    public IReadOnlyList<QueryTransformOperation> Query { get; }
    public RequestJsonBodyConfiguration? RequestBody { get; }
    public ResponseJsonBodyConfiguration? ResponseBody { get; }
    public TransformConfiguration(IReadOnlyList<HeaderTransformOperation> requestHeaders,
        IReadOnlyList<HeaderTransformOperation> responseHeaders, PathTransformConfiguration? path,
        IReadOnlyList<QueryTransformOperation> query, RequestJsonBodyConfiguration? requestBody,
        ResponseJsonBodyConfiguration? responseBody)
    {
        RequestHeaders = Array.AsReadOnly(requestHeaders.ToArray()); ResponseHeaders = Array.AsReadOnly(responseHeaders.ToArray());
        Path = path; Query = Array.AsReadOnly(query.ToArray()); RequestBody = requestBody; ResponseBody = responseBody;
    }
}

public sealed record TransformPreviewRequest(PolicyScopeRequest Scope, string Config, string Direction, string SampleJson);
public sealed record TransformPreviewDto(string OutputJson, int OperationCount, string Kind = "JsonMappingPreview", bool ActualForwarding = false);
