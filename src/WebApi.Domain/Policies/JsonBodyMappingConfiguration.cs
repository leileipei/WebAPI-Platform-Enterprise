using System.Text.Json;
using WebApi.Contracts.Common;
using WebApi.Contracts.Policies;
using static WebApi.Domain.Policies.PolicyConfigurationValidator;

namespace WebApi.Domain.Policies;

internal static class JsonBodyMappingConfiguration
{
    private static readonly HashSet<string> RequestMethods = new(StringComparer.Ordinal)
        { "GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS" };

    internal static RequestJsonBodyConfiguration? Request(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Null) return null;
        Fields(Names(root), "methods", "maxBodyBytes", "operations");
        var methods = StringArray(root, "methods", 1, 6, 7).Select(v => v.ToUpperInvariant()).ToArray();
        if (methods.Any(v => !RequestMethods.Contains(v)) || methods.Distinct().Count() != methods.Length) throw Invalid();
        return new(methods, Integer(root, "maxBodyBytes", 1024, 1048576), Operations(root.GetProperty("operations")));
    }
    internal static ResponseJsonBodyConfiguration? Response(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Null) return null;
        Fields(Names(root), "statusCodes", "maxBodyBytes", "operations");
        var array = root.GetProperty("statusCodes");
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is < 1 or > 32) throw Invalid();
        var statuses = new List<int>();
        foreach (var v in array.EnumerateArray())
        {
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var code) || code is < 200 or > 599 or 204 or 205 or 206 or 304
                || statuses.Contains(code)) throw Invalid();
            statuses.Add(code);
        }
        return new(statuses.AsReadOnly(), Integer(root, "maxBodyBytes", 1024, 1048576), Operations(root.GetProperty("operations")));
    }
    private static IReadOnlyList<JsonBodyOperation> Operations(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is < 1 or > 32) throw Invalid();
        var operations = new List<JsonBodyOperation>();
        foreach (var value in array.EnumerateArray())
        {
            var names = Names(value); var op = Text(value, "op");
            var path = Text(value, "path"); var tokens = JsonPointerPath.Parse(path);
            string? from = null; JsonElement? constant = null;
            switch (op)
            {
                case "add": case "replace":
                    Fields(names, "op", "path", "value"); constant = value.GetProperty("value").Clone();
                    if (CanonicalJson.Serialize(constant.Value).Length > 8192) throw Invalid();
                    break;
                case "remove":
                    Fields(names, "op", "path"); if (tokens.Count == 0) throw Invalid(); break;
                case "copy": case "move":
                    Fields(names, "op", "path", "from"); from = Text(value, "from"); var origin = JsonPointerPath.Parse(from);
                    if (op == "move" && tokens.Count > origin.Count && tokens.Take(origin.Count).SequenceEqual(origin, StringComparer.Ordinal)) throw Invalid();
                    break;
                default: throw Invalid();
            }
            operations.Add(new(op, path, from, constant));
        }
        return operations.AsReadOnly();
    }
}
