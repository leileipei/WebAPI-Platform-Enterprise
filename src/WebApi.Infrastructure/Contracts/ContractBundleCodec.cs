using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using WebApi.Contracts.Common;
using WebApi.Contracts.OpenApi;
namespace WebApi.Infrastructure.Contracts;
public static class ContractBundleCodec
{
    private static readonly JsonSerializerOptions Options = new() { MaxDepth = 64, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static ContractBundle Create(Uri root, IReadOnlyList<ContractDocument> documents, ContractLimits? limits = null)
    {
        limits ??= new();
        if (!root.IsAbsoluteUri || root.Fragment.Length > 0 || documents.Count == 0 || documents.Count > limits.MaxResources)
            throw Invalid("contract_bundle_budget", "来源包根或资源数量不合法。");
        var names = new HashSet<string>(StringComparer.Ordinal); long rawBytes = 0, canonicalBytes = 0; var nodes = 0;
        var reader = new ContractDocumentReader();
        var rootDoc = documents.FirstOrDefault(d => d.Source.LogicalUri == root) ?? throw Invalid("missing_contract_root", "来源包缺少根契约。");
        var snapshots = new List<ContractDocument>();
        foreach (var doc in documents) {
            var uri = doc.Source.LogicalUri;
            if (uri.OriginalString.Length > 4096) throw Invalid("contract_bundle_budget", "来源包资源 URI 超过预算。");
            if (!uri.IsAbsoluteUri || uri.Fragment.Length > 0 || !names.Add(uri.AbsoluteUri)) throw Invalid("duplicate_contract_resource", "来源包资源 URI 不合法或重复。");
            rawBytes += Encoding.UTF8.GetByteCount(doc.Source.RawText);
            canonicalBytes += Encoding.UTF8.GetByteCount(doc.CanonicalJson);
            if (rawBytes > limits.MaxBundleBytes || canonicalBytes > limits.MaxBundleBytes)
                throw Invalid("contract_bundle_budget", "来源包超过字节预算。");
            Count(doc.Root, 0);
            var parsed = uri == root ? reader.Read(doc.Source, limits, default) : reader.ReadResource(doc.Source, limits, rootDoc.Dialect, default);
            var actual = ContractDocumentReader.Canonicalize(doc.Root).ToJsonString(Options);
            if (parsed.CanonicalJson != doc.CanonicalJson || actual != doc.CanonicalJson || parsed.Dialect != doc.Dialect)
                throw Invalid("contract_bundle_tampered", "来源包内容与固定来源不一致。");
            snapshots.Add(parsed);
        }
        var payload = new { root = root.AbsoluteUri, documents = snapshots.OrderBy(d => d.Source.LogicalUri.AbsoluteUri, StringComparer.Ordinal).Select(d => new {
            uri = d.Source.LogicalUri.AbsoluteUri, format = d.Source.Format, dialect = d.Dialect.ToString(),
            rawHash = Hash(d.Source.RawText), canonical = d.CanonicalJson }) };
        return new(root, snapshots, Hash(JsonSerializer.Serialize(payload, Options)));
        void Count(JsonNode? node, int depth) {
            if (depth > limits.MaxDepth || ++nodes > limits.MaxNodes) throw Invalid("contract_bundle_budget", "来源包超过深度或总节点预算。");
            if (node is JsonObject obj) foreach (var p in obj) { if (++nodes > limits.MaxNodes) throw Invalid("contract_bundle_budget", "来源包超过总节点预算。"); Count(p.Value, depth + 1); }
            else if (node is JsonArray array) foreach (var n in array) Count(n, depth + 1);
        }
    }
    public static void Verify(ContractBundle bundle, ContractLimits? limits = null)
    {
        if (Create(bundle.RootUri, bundle.Documents, limits).Hash != bundle.Hash) throw Invalid("contract_bundle_tampered", "来源包哈希不匹配。");
    }
    internal static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static ApiException Invalid(string code, string message) => new(422, code, message);
}
