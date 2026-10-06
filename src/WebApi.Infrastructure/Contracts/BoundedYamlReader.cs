using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WebApi.Contracts.Common;
using WebApi.Contracts.OpenApi;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
namespace WebApi.Infrastructure.Contracts;

internal sealed class BoundedYamlReader
{
    private sealed class Term(NodeEvent source)
    {
        public NodeEvent Source { get; } = source;
        public List<(Scalar Key, Term Value)>? Properties { get; set; }
        public List<Term>? Items { get; set; }
        public Term? Target { get; set; }
    }
    private static readonly HashSet<string> Tags = ["tag:yaml.org,2002:str", "tag:yaml.org,2002:null", "tag:yaml.org,2002:bool", "tag:yaml.org,2002:int", "tag:yaml.org,2002:float", "tag:yaml.org,2002:map", "tag:yaml.org,2002:seq"];

    public JsonNode? Read(string text, ContractParseBudget budget, Dictionary<string, ContractIssue> locations)
    {
        var anchors = new Dictionary<string, Term>(StringComparer.Ordinal);
        var parser = new Parser(new StringReader(text));
        try {
            parser.MoveNext(); if (parser.Current is not StreamStart) throw Invalid("invalid_yaml", "YAML 流不合法。");
            parser.MoveNext(); if (parser.Current is not DocumentStart) throw Invalid("invalid_yaml", "YAML 文档不合法。");
            parser.MoveNext(); var term = Parse(0);
            if (parser.Current is not DocumentEnd) throw Invalid("invalid_yaml", "YAML 文档没有正确结束。");
            parser.MoveNext(); if (parser.Current is not StreamEnd) throw Invalid("multiple_yaml_documents", "只允许一个 YAML 文档。");
            return Build(term, "", 0, new());
        } catch (YamlException e) { throw Invalid("invalid_yaml", $"YAML 格式不合法，行 {e.Start.Line}，列 {e.Start.Column}。"); }

        Term Parse(int depth) {
            budget.Check(depth);
            if (parser.Current is AnchorAlias alias) {
                if (!anchors.TryGetValue(alias.Value.Value, out var target)) throw Invalid("unknown_yaml_alias", "YAML 别名没有已定义目标。");
                // Alias is not a NodeEvent; retain a scalar source for its location only.
                var a = new Term(new Scalar(AnchorName.Empty, TagName.Empty, "", ScalarStyle.Plain, true, true, alias.Start, alias.End)) { Target = target }; parser.MoveNext(); return a;
            }
            if (parser.Current is not NodeEvent node) throw Invalid("invalid_yaml", "YAML 节点不合法。");
            if (!node.Tag.IsEmpty && !Tags.Contains(node.Tag.Value)) throw Invalid("unsupported_yaml_tag", "不支持自定义 YAML 类型标签。");
            if (!node.Tag.IsEmpty && (node is MappingStart && node.Tag.Value != "tag:yaml.org,2002:map"
                || node is SequenceStart && node.Tag.Value != "tag:yaml.org,2002:seq"
                || node is Scalar && node.Tag.Value is "tag:yaml.org,2002:map" or "tag:yaml.org,2002:seq"))
                throw Invalid("invalid_yaml_scalar", "YAML 类型标签与节点不匹配。");
            var result = new Term(node);
            if (!node.Anchor.IsEmpty && !anchors.TryAdd(node.Anchor.Value, result)) throw Invalid("duplicate_yaml_anchor", "YAML 锚点重复。");
            parser.MoveNext();
            if (node is MappingStart) {
                result.Properties = []; var names = new HashSet<string>(StringComparer.Ordinal);
                while (parser.Current is not MappingEnd) {
                    budget.Check(depth + 1);
                    if (parser.Current is not Scalar key || !key.Tag.IsEmpty && key.Tag.Value != "tag:yaml.org,2002:str")
                        throw Invalid("invalid_yaml_key", "YAML mapping key 必须是字符串。");
                    if (!names.Add(key.Value)) throw Invalid("duplicate_key", $"YAML 存在重复键，行 {key.Start.Line}。");
                    parser.MoveNext(); result.Properties.Add((key, Parse(depth + 1)));
                }
                parser.MoveNext();
            } else if (node is SequenceStart) {
                result.Items = [];
                while (parser.Current is not SequenceEnd) result.Items.Add(Parse(depth + 1));
                parser.MoveNext();
            } else if (node is not Scalar) throw Invalid("invalid_yaml", "不支持的 YAML 节点。");
            return result;
        }

        JsonNode? Build(Term term, string pointer, int depth, HashSet<Term> active) {
            budget.Check(depth);
            if (!active.Add(term)) throw Invalid("yaml_alias_cycle", "YAML 别名形成循环。");
            try {
                locations.TryAdd(pointer, new("source_location", pointer, "", checked((int)term.Source.Start.Line), checked((int)term.Source.Start.Column)));
                if (term.Target is not null) return Build(term.Target, pointer, depth + 1, active);
                if (term.Properties is not null) {
                    var obj = new JsonObject();
                    foreach (var (key, value) in term.Properties) {
                        var child = pointer + "/" + ContractDocumentReader.Escape(key.Value);
                        locations.TryAdd(child, new("source_location", child, "", checked((int)key.Start.Line), checked((int)key.Start.Column)));
                        var built = Build(value, child, depth + 1, active);
                        if (key.Value == "<<") {
                            if (built is JsonObject merged) Merge(merged);
                            else if (built is JsonArray array) foreach (var item in array) {
                                if (item is not JsonObject m) throw Invalid("invalid_yaml_merge", "合并项必须是 mapping。"); Merge(m);
                            } else throw Invalid("invalid_yaml_merge", "合并项必须是 mapping 或 mapping 序列。");
                        } else {
                            if (obj.ContainsKey(key.Value)) throw Invalid("duplicate_key", "YAML 合并结果存在重复键。");
                            obj.Add(key.Value, built);
                        }
                    }
                    return obj;
                    void Merge(JsonObject source) {
                        foreach (var p in source) {
                            budget.Check(depth + 1);
                            if (obj.ContainsKey(p.Key)) throw Invalid("duplicate_key", "YAML 合并结果存在重复键。");
                            obj.Add(p.Key, p.Value?.DeepClone());
                        }
                    }
                }
                if (term.Items is not null) return new JsonArray(term.Items.Select((t, i) => Build(t, pointer + "/" + i, depth + 1, active)).ToArray());
                return ScalarValue((Scalar)term.Source);
            } finally { active.Remove(term); }
        }
    }

    private static JsonNode? ScalarValue(Scalar scalar)
    {
        var value = scalar.Value; var tag = scalar.Tag.IsEmpty ? "" : scalar.Tag.Value;
        if (tag == "tag:yaml.org,2002:str" || tag.Length == 0 && scalar.Style != ScalarStyle.Plain) return JsonValue.Create(value);
        void RequireTag(params string[] allowed) {
            if (tag.Length > 0 && !allowed.Contains(tag, StringComparer.Ordinal)) throw Invalid("invalid_yaml_scalar", "YAML 类型标签与标量内容不匹配。");
        }
        if (value is "" or "~" or "null" or "Null" or "NULL") { RequireTag("tag:yaml.org,2002:null"); return null; }
        if (value is "true" or "True" or "TRUE") { RequireTag("tag:yaml.org,2002:bool"); return JsonValue.Create(true); }
        if (value is "false" or "False" or "FALSE") { RequireTag("tag:yaml.org,2002:bool"); return JsonValue.Create(false); }
        if (Regex.IsMatch(value, "^[+-]?(?:[0-9]+(?:\\.[0-9]*)?|\\.[0-9]+)(?:[eE][+-]?[0-9]+)?$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) {
            RequireTag(value.IndexOfAny(['.', 'e', 'E']) >= 0 ? "tag:yaml.org,2002:float" : "tag:yaml.org,2002:int", "tag:yaml.org,2002:float");
            var number = value.TrimStart('+'); var negative = number.StartsWith('-'); if (negative) number = number[1..];
            var exponent = number.IndexOfAny(['e', 'E']); var suffix = exponent < 0 ? "" : number[exponent..]; if (exponent >= 0) number = number[..exponent];
            var dot = number.IndexOf('.'); var integer = (dot < 0 ? number : number[..dot]).TrimStart('0'); if (integer.Length == 0) integer = "0";
            var fraction = dot < 0 ? "" : number[dot..]; if (fraction == ".") fraction = ".0";
            return JsonNode.Parse((negative ? "-" : "") + integer + fraction + suffix);
        }
        if (Regex.IsMatch(value, "^[+-]?0[xo][0-9a-fA-F]+$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) {
            RequireTag("tag:yaml.org,2002:int");
            var n = value.TrimStart('+', '-'); var radix = n[1] == 'x' ? 16 : 8; BigInteger integer = 0;
            foreach (var c in n[2..]) { var digit = c <= '9' ? c - '0' : char.ToLowerInvariant(c) - 'a' + 10; if (digit >= radix) throw Invalid("invalid_yaml_scalar", "YAML 数字不合法。"); integer = integer * radix + digit; }
            if (value.StartsWith('-')) integer = -integer;
            return JsonNode.Parse(integer.ToString(CultureInfo.InvariantCulture));
        }
        if (tag.Length > 0 && tag != "tag:yaml.org,2002:str") throw Invalid("invalid_yaml_scalar", "YAML 类型标签与标量内容不匹配。");
        if (value.ToLowerInvariant() is ".inf" or "+.inf" or "-.inf" or ".nan") throw Invalid("invalid_yaml_scalar", "JSON 契约不允许非有限数字。");
        return JsonValue.Create(value);
    }
    private static ApiException Invalid(string code, string message) => new(422, code, message);
}
