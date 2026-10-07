using System.Text.Json.Nodes;
using System.Text;
using System.Text.RegularExpressions;
using WebApi.Contracts.Common;
using WebApi.Contracts.OpenApi;
namespace WebApi.Infrastructure.Contracts;
public sealed record ResolvedContractNode(Uri ResourceUri, string Pointer, JsonNode Node);
public sealed record ContractReference(Uri ResourceUri,string Pointer,string Reference);
public sealed class ContractReferenceRegistry
{
    private sealed record Identity(Uri Resource, string Pointer, JsonNode Node);
    private readonly Dictionary<string, JsonNode> resources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Identity> anchors = new(StringComparer.Ordinal);
    private readonly Dictionary<JsonNode, Identity> identities = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<JsonNode, ContractDialect> physicalDialects = new(ReferenceEqualityComparer.Instance);
    public ContractDialect DialectOf(JsonNode node) { while (node.Parent is not null) node = node.Parent; return physicalDialects.TryGetValue(node, out var dialect) ? dialect : throw Missing(); }
    private readonly HashSet<JsonNode> schemaNodes = new(ReferenceEqualityComparer.Instance);
    private readonly int maximumIndexBytes;
    private long indexBytes;
    private long pointerBytes;
    private readonly List<ContractReference> references=[];
    public IReadOnlyList<ContractReference> References => references.AsReadOnly();
    public ResolvedContractNode Describe(JsonNode node) => identities.TryGetValue(node,out var identity) ? new(identity.Resource,identity.Pointer,node) : throw Missing();
    public IReadOnlyList<ResolvedContractNode> Resources => resources.Select(p => new ResolvedContractNode(new(p.Key), identities[p.Value].Pointer, p.Value)).ToArray();
    internal IReadOnlyList<ResolvedContractNode> SchemaRoots => schemaNodes.Where(node =>
        node is JsonObject obj && obj.ContainsKey("$id") || !HasSchemaAncestor(node)).Select(Describe).ToArray();
    private bool HasSchemaAncestor(JsonNode node)
    {
        for(var parent=node.Parent;parent is not null;parent=parent.Parent)if(schemaNodes.Contains(parent))return true;
        return false;
    }
    public ContractReferenceRegistry(ContractBundle bundle, ContractLimits limits)
    {
        maximumIndexBytes = limits.MaxBundleBytes;
        ContractBundleCodec.Verify(bundle, limits);
        var openApiRoots=FindOpenApiRoots(bundle);
        // Own this graph. A caller changing the original bundle cannot change an indexed resource.
        foreach (var doc in bundle.Documents) {
            var root = doc.Root.DeepClone();
            physicalDialects.Add(root, doc.Dialect);
            Register(doc.Source.LogicalUri, root);
            Walk(root, "", doc.Source.LogicalUri, !openApiRoots.Contains(doc.Source.LogicalUri.AbsoluteUri), doc.Dialect);
        }
    }
    private static HashSet<string> FindOpenApiRoots(ContractBundle bundle)
    {
        // A reference used as a Parameter/Response/Path Item identifies an external OpenAPI
        // object. Its nested `schema` is a schema context; the containing object is not.
        var names=bundle.Documents.Select(x=>x.Source.LogicalUri.AbsoluteUri).ToHashSet(StringComparer.Ordinal);
        var result=bundle.Documents.Where(x=>x.Root is JsonObject o&&o.ContainsKey("openapi")).Select(x=>x.Source.LogicalUri.AbsoluteUri).ToHashSet(StringComparer.Ordinal);
        bool changed;
        do{
            changed=false;
            foreach(var doc in bundle.Documents)Scan(doc.Root,"",doc.Source.LogicalUri,!result.Contains(doc.Source.LogicalUri.AbsoluteUri),false,doc.Dialect);
        }while(changed);
        return result;
        void Scan(JsonNode? node,string pointer,Uri basis,bool schema,bool data,ContractDialect dialect)
        {
            if(node is JsonObject obj){
                if(schema&&dialect==ContractDialect.Oas31&&obj["$id"] is JsonValue id&&id.TryGetValue<string>(out var idText)&&Uri.TryCreate(basis,idText,out var idUri))basis=idUri;
                if(!schema&&!data&&obj["$ref"] is JsonValue reference&&reference.TryGetValue<string>(out var text)&&Uri.TryCreate(basis,text,out var uri)&&uri.Fragment.Length==0&&names.Contains(uri.AbsoluteUri)&&result.Add(uri.AbsoluteUri))changed=true;
                foreach(var(key,value)in obj){var child=pointer+"/"+ContractDocumentReader.Escape(key);
                    if(schema&&SchemaNavigation.MapKeywords.Contains(key)&&value is JsonObject map)foreach(var(name,item)in map)Scan(item,child+"/"+ContractDocumentReader.Escape(name),basis,true,data,dialect);
                    else if(schema&&SchemaNavigation.ArrayKeywords.Contains(key)&&value is JsonArray list)for(var i=0;i<list.Count;i++)Scan(list[i],child+"/"+i,basis,true,data,dialect);
                    else if(!schema&&child=="/components/schemas"&&value is JsonObject components)foreach(var(name,item)in components)Scan(item,child+"/"+ContractDocumentReader.Escape(name),basis,true,data,dialect);
                    else Scan(value,child,basis,schema?SchemaNavigation.SingleKeywords.Contains(key):key=="schema"&&!data,data||key is "example" or "examples" or "default" or "const" or "enum"||key.StartsWith("x-",StringComparison.Ordinal),dialect);
                }
            }else if(node is JsonArray array)for(var i=0;i<array.Count;i++)Scan(array[i],pointer+"/"+i,basis,false,data,dialect);
        }
    }
    public ResolvedContractNode Resolve(Uri currentResource, string reference)
    {
        if (reference.Any(char.IsControl) || reference != reference.Trim() || !Uri.TryCreate(currentResource, reference, out var uri)) throw Missing();
        var resource = WithoutFragment(uri);
        if (!resources.TryGetValue(resource.AbsoluteUri, out var root)) throw Missing();
        var fragment = uri.Fragment;
        if (Regex.IsMatch(fragment, "%(?![0-9a-fA-F]{2})", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) throw Missing();
        fragment = fragment.Length > 0 ? Uri.UnescapeDataString(fragment[1..]) : "";
        if (fragment.Length > 0 && !fragment.StartsWith('/')) {
            if (!anchors.TryGetValue(resource.AbsoluteUri + "#" + fragment, out var anchor)) throw Missing();
            return new(anchor.Resource, anchor.Pointer, anchor.Node);
        }
        JsonNode? node = root;
        if (fragment.Length > 0) foreach (var token in fragment[1..].Split('/')) {
            if (Regex.IsMatch(token, "~(?![01])", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) throw Missing();
            var key = token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (node is JsonObject obj) { if (!obj.TryGetPropertyValue(key, out node)) throw Missing(); }
            else if (node is JsonArray array && (key == "0" || key.Length > 0 && key[0] is >= '1' and <= '9') && int.TryParse(key, out var i) && i >= 0 && i < array.Count) node = array[i];
            else throw Missing();
        }
        if (node is null || !identities.TryGetValue(node, out var found)) throw Missing();
        return new(found.Resource, fragment, node);
    }
    private void Register(Uri uri, JsonNode root)
    {
        if (uri.OriginalString.Length > 4096) throw IndexTooLarge();
        if (!uri.IsAbsoluteUri || uri.Fragment.Length > 0) throw new ApiException(422, "invalid_schema_id", "Schema 资源 ID 必须是无 fragment 的资源 URI。");
        if (resources.TryGetValue(uri.AbsoluteUri, out var existing) && !ReferenceEquals(existing, root))
            throw new ApiException(422, "duplicate_schema_id", "来源包含冲突的资源 ID。");
        if (!resources.ContainsKey(uri.AbsoluteUri)) CountIndex(uri.AbsoluteUri);
        resources[uri.AbsoluteUri] = root;
    }
    private void Walk(JsonNode? node, string pointer, Uri basis, bool schema, ContractDialect dialect,bool data=false)
    {
        if (node is null) return;
        if(schema&&!data)schemaNodes.Add(node);
        if (schema && !data && dialect == ContractDialect.Oas31 && node is JsonObject s && s.TryGetPropertyValue("$id", out var id)) {
            var text = ContractDocumentReader.Text(id);
            if (text.Length > 4096) throw IndexTooLarge();
            if (text.Length == 0 || !Uri.TryCreate(basis, text, out var uri)) throw new ApiException(422, "invalid_schema_id", "Schema 资源 ID 不合法。");
            basis = uri; Register(basis, node);
        }
        var identity = AddIdentity(node, basis, pointer);
        if(!data && node is JsonObject refObject) foreach(var keyword in schema ? new[]{"$ref","$dynamicRef"} : new[]{"$ref"})
            if(refObject.TryGetPropertyValue(keyword,out var reference)) {
                if(reference is not JsonValue textNode || !textNode.TryGetValue<string>(out var text)) throw Missing();
                references.Add(new(basis,pointer+"/"+keyword,text));
            }
        if (schema && !data && dialect == ContractDialect.Oas31 && node is JsonObject a) foreach (var keyword in new[] { "$anchor", "$dynamicAnchor" }) {
            if (!a.TryGetPropertyValue(keyword, out var value)) continue;
            var name = ContractDocumentReader.Text(value);
            if (name.Length > 256) throw IndexTooLarge();
            if (!Regex.IsMatch(name, "^[A-Za-z_][-A-Za-z0-9._]*$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                throw new ApiException(422, "invalid_schema_anchor", "Schema anchor 不合法。");
            var key = basis.AbsoluteUri + "#" + name;
            if (anchors.TryGetValue(key, out var previous) && !ReferenceEquals(previous.Node, node))
                throw new ApiException(422, "duplicate_schema_anchor", "同一资源中的 anchor 重复。");
            if (!anchors.ContainsKey(key)) CountIndex(key);
            anchors[key] = identity;
        }
        if (node is JsonObject obj) foreach (var (key, value) in obj) {
            var childPointer = pointer + "/" + ContractDocumentReader.Escape(key);
            if (data || schema && dialect == ContractDialect.Oas30 && obj.ContainsKey("$ref")) {
                // Keep raw identities for provenance; ignored siblings are never schema contexts.
                Walk(value, childPointer, basis, false, dialect, true);
                continue;
            }
            if (schema && SchemaNavigation.MapKeywords.Contains(key) && value is JsonObject map) {
                AddIdentity(map, basis, childPointer);
                foreach (var (name, child) in map) Walk(child, childPointer + "/" + ContractDocumentReader.Escape(name), basis, true, dialect);
            } else if (schema && SchemaNavigation.ArrayKeywords.Contains(key) && value is JsonArray list) {
                AddIdentity(list, basis, childPointer);
                for (var i = 0; i < list.Count; i++) Walk(list[i], childPointer + "/" + i, basis, true, dialect);
            } else if (!schema && childPointer == "/components/schemas" && value is JsonObject schemas) {
                AddIdentity(schemas, basis, childPointer);
                foreach (var (name, child) in schemas) Walk(child, childPointer + "/" + ContractDocumentReader.Escape(name), basis, true, dialect);
            } else Walk(value, childPointer, basis,
                schema ? SchemaNavigation.SingleKeywords.Contains(key) : key == "schema" && !data, dialect,
                data || key is "example" or "examples" or "default" or "const" or "enum" || key.StartsWith("x-",StringComparison.Ordinal));
        } else if (node is JsonArray array) for (var i = 0; i < array.Count; i++) Walk(array[i], pointer + "/" + i, basis, false, dialect,data);
    }
    private static Uri WithoutFragment(Uri uri) => new(uri.AbsoluteUri.Split('#')[0], UriKind.Absolute);
    private Identity AddIdentity(JsonNode node, Uri basis, string pointer)
    {
        if ((pointerBytes += Encoding.UTF8.GetByteCount(pointer)) > maximumIndexBytes) throw IndexTooLarge();
        var identity = new Identity(basis, pointer, node); identities.Add(node, identity); return identity;
    }
    private void CountIndex(string key) { if ((indexBytes += Encoding.UTF8.GetByteCount(key)) > maximumIndexBytes) throw IndexTooLarge(); }
    private static ApiException IndexTooLarge() => new(422, "contract_bundle_budget", "引用资源 ID、anchor 或 Pointer 索引超过预算。");
    private static ApiException Missing() => new(422, "missing_contract_reference", "引用未包含在固定来源包中，或目标 Pointer/anchor 不存在。");
}

internal static class SchemaNavigation
{
    internal static readonly HashSet<string> MapKeywords = ["properties", "patternProperties", "dependentSchemas", "$defs", "definitions"];
    internal static readonly HashSet<string> ArrayKeywords = ["allOf", "anyOf", "oneOf", "prefixItems"];
    internal static readonly HashSet<string> SingleKeywords = ["additionalProperties", "unevaluatedProperties", "unevaluatedItems", "propertyNames", "contains", "items", "not", "if", "then", "else", "contentSchema"];
    internal static bool IsDataPointer(string pointer) => pointer.Split('/').Any(p => p is "example" or "examples" or "default" or "const" or "enum" or "value" || p.StartsWith("x-", StringComparison.Ordinal));
}
