using System.Text;
using System.Text.Json;
using WebApi.Contracts.Policies;
using static WebApi.Domain.Policies.PolicyConfigurationValidator;

namespace WebApi.Domain.Policies;

internal static class TransformPolicyConfiguration
{
    private static readonly HashSet<string> RepresentationHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "X-API-Key", "Content-Length", "Content-Type", "Content-Encoding", "Vary", "Cache-Control", "Age", "Expires",
        "ETag", "Last-Modified", "Date", "Content-MD5", "Digest", "Content-Digest", "Repr-Digest", "Signature", "Signature-Input"
    };
    internal static TransformConfiguration Parse(JsonElement root)
    {
        Fields(Names(root), "requestHeaders", "responseHeaders", "path", "query", "requestBody", "responseBody");
        var request = Headers(root.GetProperty("requestHeaders")); var response = Headers(root.GetProperty("responseHeaders"));
        var path = Path(root.GetProperty("path")); var query = Query(root.GetProperty("query"));
        var requestBody = JsonBodyMappingConfiguration.Request(root.GetProperty("requestBody"));
        var responseBody = JsonBodyMappingConfiguration.Response(root.GetProperty("responseBody"));
        if (request.Count == 0 && response.Count == 0 && path is null && query.Count == 0 && requestBody is null && responseBody is null) throw Invalid();
        return new(request, response, path, query, requestBody, responseBody);
    }
    private static IReadOnlyList<HeaderTransformOperation> Headers(JsonElement array)
    {
        var result = new List<HeaderTransformOperation>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Entries(array))
        {
            var operation = Action(item); var name = Name(item, 128);
            if (!name.All(CorsPolicyConfiguration.IsTokenChar) || name == "*" || CorsPolicyConfiguration.IsProtectedHeader(name)
                || RepresentationHeaders.Contains(name) || name.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase)
                || !names.Add(name)) throw Invalid();
            result.Add(new(operation, name, operation == TransformAction.Set ? Value(item) : null));
        }
        return result.AsReadOnly();
    }
    private static IReadOnlyList<QueryTransformOperation> Query(JsonElement array)
    {
        var result = new List<QueryTransformOperation>(); var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Entries(array))
        {
            var operation = Action(item); var name = Name(item, 128);
            if (!names.Add(name)) throw Invalid();
            result.Add(new(operation, name, operation == TransformAction.Set ? Value(item) : null));
        }
        return result.AsReadOnly();
    }
    private static JsonElement.ArrayEnumerator Entries(JsonElement value)
    { if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 32) throw Invalid(); return value.EnumerateArray(); }
    private static TransformAction Action(JsonElement item)
    {
        var names = Names(item); var action = Text(item, "action");
        if (action == "Set") { Fields(names, "action", "name", "value"); return TransformAction.Set; }
        if (action == "Remove") { Fields(names, "action", "name"); return TransformAction.Remove; }
        throw Invalid();
    }
    private static string Name(JsonElement item, int max)
    { var name = Text(item, "name"); if (name.Length is < 1 || name.Length > max || name.Any(char.IsControl)) throw Invalid(); return name; }
    private static string Value(JsonElement item)
    { var value = Text(item, "value"); if (Encoding.UTF8.GetByteCount(value) > 1024 || value.Any(char.IsControl)) throw Invalid(); return value; }
    private static PathTransformConfiguration? Path(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        Fields(Names(value), "removePrefix", "addPrefix");
        var remove = Prefix(value.GetProperty("removePrefix")); var add = Prefix(value.GetProperty("addPrefix"));
        if (remove is null && add is null) throw Invalid();
        return new(remove, add);
    }
    private static string? Prefix(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw Invalid();
        var path = value.GetString()!;
        if (path.Length is < 1 or > 512 || path[0] != '/' || path.Any(char.IsControl) || path.Contains("//", StringComparison.Ordinal)
            || path.IndexOfAny(['%', '?', '#', '\\', '{', '}']) >= 0 || path.Split('/').Any(v => v is "." or "..")) throw Invalid();
        return path == "/" ? path : path.TrimEnd('/');
    }
}
