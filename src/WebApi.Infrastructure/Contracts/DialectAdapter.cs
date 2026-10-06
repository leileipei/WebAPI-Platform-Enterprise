using System.Text.Json.Nodes;
using WebApi.Contracts.Common;
using WebApi.Contracts.OpenApi;
namespace WebApi.Infrastructure.Contracts;
public sealed record PreparedSchema(JsonNode Node, IReadOnlyList<ContractIssue> Issues);
public static class DialectAdapter
{
    private static readonly HashSet<string> Known = [
        "$schema", "$id", "$anchor", "$dynamicAnchor", "$ref", "$dynamicRef", "$defs", "$comment", "$vocabulary", "definitions",
        "type", "enum", "const", "multipleOf", "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "minLength", "maxLength", "pattern", "format",
        "items", "prefixItems", "contains", "minContains", "maxContains", "minItems", "maxItems", "uniqueItems", "unevaluatedItems", "additionalProperties",
        "properties", "patternProperties", "required", "minProperties", "maxProperties", "dependentRequired", "dependentSchemas", "propertyNames", "unevaluatedProperties",
        "allOf", "anyOf", "oneOf", "not", "if", "then", "else", "title", "description", "default", "examples", "example", "deprecated", "readOnly", "writeOnly",
        "contentEncoding", "contentMediaType", "contentSchema", "discriminator", "xml", "externalDocs"
    ];
    private static readonly HashSet<string> Modern = ["$id", "$anchor", "$dynamicAnchor", "$dynamicRef", "$defs", "$vocabulary", "const", "prefixItems", "contains", "minContains", "maxContains", "unevaluatedItems", "patternProperties", "dependentRequired", "dependentSchemas", "propertyNames", "unevaluatedProperties", "if", "then", "else", "contentSchema"];
    public static PreparedSchema PrepareSchema(JsonNode schema, ContractDialect dialect, string direction)
    {
        if (direction is not ("request" or "response")) throw new ApiException(422, "invalid_schema_direction", "校验方向必须为 request 或 response。");
        var nodes = 0;
        Count(schema, 0);
        var issues = new List<ContractIssue>();
        return new(Prepare(schema, "", 0), issues);
        JsonNode Prepare(JsonNode node, string pointer, int depth) {
            if (depth > 64) throw new ApiException(422, "contract_budget", "Schema 深度超过预算。");
            if (node is JsonValue b && b.TryGetValue<bool>(out _)) {
                if (dialect == ContractDialect.Oas30) Issue("invalid_schema_dialect", pointer, "OpenAPI 3.0 Schema 必须为对象。");
                return node.DeepClone();
            }
            if (node is not JsonObject original) { Issue("invalid_schema_dialect", pointer, "Schema 必须为对象或 boolean。"); return node.DeepClone(); }
            if (dialect == ContractDialect.Oas30 && original.ContainsKey("$ref")) return new JsonObject { ["$ref"] = original["$ref"]?.DeepClone() };
            var obj = (JsonObject)original.DeepClone();
            foreach (var (key, value) in original) {
                var child = pointer + "/" + ContractDocumentReader.Escape(key);
                if (!Known.Contains(key) && !(dialect == ContractDialect.Oas30 && key == "nullable")) Issue("unsupported_schema_keyword", child, "未登记的 Schema 关键字，原字段已保留。");
                if (dialect == ContractDialect.Oas30 && Modern.Contains(key)) Issue("invalid_schema_dialect", child, "该关键字不属于 OpenAPI 3.0 Schema 方言。");
                if (key == "$schema" && ContractDocumentReader.Text(value) is not ("https://json-schema.org/draft/2020-12/schema" or "https://spec.openapis.org/oas/3.1/dialect/base"))
                    Issue("unsupported_schema_dialect", child, "Schema 声明了未登记方言。");
                if (SchemaNavigation.MapKeywords.Contains(key) && value is JsonObject map) {
                    var prepared = new JsonObject(); foreach (var (name, item) in map) {
                        if (item is null) { Issue("invalid_schema_dialect", child, "Schema 不能为 null。"); prepared[name] = null; }
                        else prepared[name] = Prepare(item, child + "/" + ContractDocumentReader.Escape(name), depth + 1);
                    } obj[key] = prepared;
                } else if (SchemaNavigation.ArrayKeywords.Contains(key) && value is JsonArray list) {
                    var prepared = new JsonArray(); for (var i = 0; i < list.Count; i++) {
                        if (list[i] is null) { Issue("invalid_schema_dialect", child, "Schema 不能为 null。"); prepared.Add((JsonNode?)null); }
                        else prepared.Add(Prepare(list[i]!, child + "/" + i, depth + 1));
                    } obj[key] = prepared;
                } else if (SchemaNavigation.SingleKeywords.Contains(key) && value is not null) obj[key] = Prepare(value, child, depth + 1);
            }
            if (dialect == ContractDialect.Oas30) {
                if (obj["nullable"] is JsonValue n && n.TryGetValue<bool>(out var nullable) && nullable && obj["type"] is JsonValue t && t.TryGetValue<string>(out var type)) obj["type"] = new JsonArray(type, "null");
                obj.Remove("nullable");
                foreach (var (exclusive, bound) in new[] { ("exclusiveMinimum", "minimum"), ("exclusiveMaximum", "maximum") }) {
                    if (obj[exclusive] is JsonValue v && v.TryGetValue<bool>(out var enabled)) {
                        obj.Remove(exclusive);
                        if (enabled) {
                            if (obj[bound] is null) Issue("invalid_schema_dialect", pointer + "/" + exclusive, "独占边界缺少对应数值。");
                            else { obj[exclusive] = obj[bound]!.DeepClone(); obj.Remove(bound); }
                        }
                    }
                }
            }
            if (obj["required"] is JsonArray required && obj["properties"] is JsonObject properties) for (var i = required.Count - 1; i >= 0; i--) {
                var name = ContractDocumentReader.Text(required[i]);
                if (properties[name] is JsonObject property && property[direction == "request" ? "readOnly" : "writeOnly"] is JsonValue flag && flag.TryGetValue<bool>(out var enabled) && enabled) required.RemoveAt(i);
            }
            return obj;
        }
        void Issue(string code, string pointer, string message) { if (issues.Count >= 500) throw new ApiException(422, "contract_budget", "Schema 诊断超过预算。"); issues.Add(new(code, pointer, message)); }
        void Count(JsonNode? node, int depth) {
            if (depth > 64 || ++nodes > 50000) throw new ApiException(422, "contract_budget", "Schema 深度或节点数量超过预算。");
            if (node is JsonObject obj) foreach (var p in obj) { if (++nodes > 50000) throw new ApiException(422, "contract_budget", "Schema 节点数量超过预算。"); Count(p.Value, depth + 1); }
            else if (node is JsonArray array) foreach (var item in array) Count(item, depth + 1);
        }
    }
}
