using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WebApi.Contracts.Common;
using WebApi.Contracts.OpenApi;
namespace WebApi.Infrastructure.Contracts;
public sealed class ContractDocumentReader
{
    // The parse budget validates depth before canonical serialization.
    private static readonly JsonSerializerOptions Options = new() { MaxDepth = 128, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly string[] Methods = ["get", "post", "put", "patch", "delete", "head", "options", "trace"];

    public ContractDocument ReadResource(ContractSource source, ContractLimits limits, ContractDialect dialect, CancellationToken ct) => ReadCore(source, limits, dialect, ct);

    public ContractDocument Read(ContractSource source, ContractLimits limits, CancellationToken ct)
        => ReadCore(source, limits, null, ct);

    private static ContractDocument ReadCore(ContractSource source, ContractLimits limits, ContractDialect? inheritedDialect, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (source.RawText is null || Encoding.UTF8.GetByteCount(source.RawText) > limits.MaxDocumentBytes)
            throw new ApiException(413, "contract_too_large", "契约文档超过允许字节数。");
        var locations = new Dictionary<string, ContractIssue>(StringComparer.Ordinal);
        var budget = new ContractParseBudget(limits, ct);
        var format = source.Format.ToLowerInvariant();
        if (format is "auto" or "") format = source.RawText.TrimStart().StartsWith('{') ? "json" : "yaml";
        JsonNode? node = format switch {
            "json" => ParseJson(source.RawText, budget, locations),
            "yaml" or "yml" => new BoundedYamlReader().Read(source.RawText, budget, locations),
            _ => throw new ApiException(422, "unsupported_contract_format", "契约格式必须为 JSON 或 YAML。")
        };
        var dialect = inheritedDialect;
        if (inheritedDialect is null || node is JsonObject o && o.ContainsKey("openapi")) {
            if (node is not JsonObject root) throw Invalid("契约根必须是对象。");
            var version = Text(root["openapi"]);
            if (!Regex.IsMatch(version, "^3\\.(0|1)\\.[0-9]+$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                throw new ApiException(422, "unsupported_openapi", "仅支持 OpenAPI 3.0.x 或 3.1.x。");
            ValidateShape(root, limits);
            dialect = version.StartsWith("3.0.", StringComparison.Ordinal) ? ContractDialect.Oas30 : ContractDialect.Oas31;
        } else if (node is not JsonObject && !(node is JsonValue value && value.TryGetValue<bool>(out _)))
            throw Invalid("引用资源必须是对象或 boolean Schema。");
        var canonical = Canonicalize(node!).ToJsonString(Options);
        if (Encoding.UTF8.GetByteCount(canonical) > limits.MaxDocumentBytes)
            throw new ApiException(413, "contract_too_large", "规范化契约超过允许字节数。");
        return new(source with { Format = format == "yml" ? "yaml" : format }, node!, dialect!.Value, canonical, locations);
    }

    internal static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    internal static string Escape(string value) => value.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
    internal static JsonNode Canonicalize(JsonNode node)
    {
        if (node is JsonObject obj) {
            var sorted = new JsonObject();
            foreach (var p in obj.OrderBy(p => p.Key, StringComparer.Ordinal)) sorted[p.Key] = p.Value is null ? null : Canonicalize(p.Value);
            return sorted;
        }
        if (node is JsonArray array) return new JsonArray(array.Select(n => n is null ? null : Canonicalize(n)).ToArray());
        return node.DeepClone();
    }
    private static void ValidateShape(JsonObject root, ContractLimits limits)
    {
        if (root["info"] is not JsonObject info || Text(info["title"]).Length == 0 || Text(info["version"]).Length == 0)
            throw Invalid("info 必须含非空 title 和 version。");
        if (root["paths"] is not JsonObject paths) throw Invalid("paths 必须是对象。");
        var operations = 0;
        foreach (var (path, item) in paths) {
            if (!path.StartsWith('/') || item is not JsonObject entry) throw Invalid("Path Item 或路径不合法。");
            foreach (var method in Methods) {
                if (!entry.ContainsKey(method)) continue;
                if (++operations > limits.MaxOperations) throw new ApiException(422, "contract_budget", "Operation 数量超过解析预算。");
                if (entry[method] is not JsonObject operation || operation["responses"] is not JsonObject responses || responses.Count == 0)
                    throw Invalid("Operation 必须含非空 responses 对象。");
                foreach (var (status, response) in responses) {
                    if (!(status == "default" || Regex.IsMatch(status, "^[1-5]([0-9]{2}|[xX]{2})$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))))
                        throw Invalid("响应状态码不合法。");
                    if (response is not JsonObject r || !r.ContainsKey("$ref") && Text(r["description"]).Length == 0)
                        throw Invalid("Response 必须为对象并包含 description 或引用。");
                }
            }
        }
    }
    private static ApiException Invalid(string message) => new(422, "invalid_contract", message);
    private sealed class Frame(string pointer, bool isObject)
    {
        public string Pointer { get; } = pointer;
        public bool IsObject { get; } = isObject;
        public string? Property { get; set; }
        public int Index { get; set; }
        public HashSet<string> Names { get; } = new(StringComparer.Ordinal);
    }
    private static JsonNode? ParseJson(string text, ContractParseBudget budget, Dictionary<string, ContractIssue> locations)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var lines = new List<int> { 0 }; for (var i = 0; i < bytes.Length; i++) if (bytes[i] == 10) lines.Add(i + 1);
        var frames = new Stack<Frame>();
        void Locate(string pointer, long position) {
            var index = lines.BinarySearch((int)position); if (index < 0) index = ~index - 1;
            budget.RecordLocation(locations, pointer, index + 1, (int)position - lines[index] + 1);
        }
        try {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = budget.Limits.MaxDepth });
            while (reader.Read()) {
                budget.Check(reader.CurrentDepth);
                if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray) { frames.Pop(); continue; }
                if (reader.TokenType == JsonTokenType.PropertyName) {
                    var frame = frames.Peek(); var key = reader.GetString()!;
                    if (!frame.Names.Add(key)) throw new ApiException(422, "duplicate_key", "JSON 存在重复键。");
                    frame.Property = key; Locate(frame.Pointer + "/" + Escape(key), reader.TokenStartIndex); continue;
                }
                var pointer = "";
                if (frames.TryPeek(out var parent)) pointer = parent.Pointer + "/" + (parent.IsObject ? Escape(parent.Property!) : (parent.Index++).ToString(System.Globalization.CultureInfo.InvariantCulture));
                Locate(pointer, reader.TokenStartIndex);
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) frames.Push(new(pointer, reader.TokenType == JsonTokenType.StartObject));
            }
            return JsonNode.Parse(bytes, new(), new() { MaxDepth = budget.Limits.MaxDepth });
        } catch (JsonException e) {
            throw new ApiException(422, "invalid_contract", $"JSON 格式或深度不合法，行 {(e.LineNumber ?? 0) + 1}，列 {(e.BytePositionInLine ?? 0) + 1}。");
        }
    }
}

internal sealed class ContractParseBudget(ContractLimits limits, CancellationToken ct)
{
    private int nodes;
    private long locationBytes;
    public ContractLimits Limits { get; } = limits;
    public void Check(int depth) {
        ct.ThrowIfCancellationRequested();
        if (depth > Limits.MaxDepth || ++nodes > Limits.MaxNodes)
            throw new ApiException(422, "contract_budget", "契约深度或节点数量超过解析预算。");
    }
    public void RecordLocation(Dictionary<string, ContractIssue> locations, string pointer, int line, int column)
    {
        if (locations.ContainsKey(pointer)) return;
        if ((locationBytes += Encoding.UTF8.GetByteCount(pointer)) > Limits.MaxDocumentBytes)
            throw new ApiException(422, "contract_budget", "契约位置索引超过解析预算。");
        locations.Add(pointer, new("source_location", pointer, "", line, column));
    }
}
