using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WebApi.Contracts.Common;
using WebApi.Contracts.Policies;

namespace WebApi.Domain.Policies;

public static class JsonBodyMapper
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions OutputOptions = new() { MaxDepth = 32 };

    public static CompiledJsonBodyMapping Compile(IReadOnlyList<JsonBodyOperation> operations)
    {
        if (operations is null || operations.Count is < 1 or > 32) throw PolicyConfigurationValidator.Invalid();
        try
        {
            // Reuse the exact saved-policy rules even for programmatically supplied operations.
            var envelope = new
            {
                requestHeaders = Array.Empty<HeaderTransformOperation>(), responseHeaders = Array.Empty<HeaderTransformOperation>(),
                path = (PathTransformConfiguration?)null, query = Array.Empty<QueryTransformOperation>(),
                requestBody = new RequestJsonBodyConfiguration(["POST"], 1048576, operations), responseBody = (ResponseJsonBodyConfiguration?)null
            };
            var validated = PolicyConfigurationValidator.ParseTransform(Encoding.UTF8.GetString(CanonicalJson.Serialize(envelope))).RequestBody!;
            return new(validated.Operations.Select(op => new CompiledJsonBodyOperation(op.Op, JsonPointerPath.Parse(op.Path),
                op.From is null ? null : JsonPointerPath.Parse(op.From), op.Value?.Clone())).ToArray());
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        { throw PolicyConfigurationValidator.Invalid(); }
    }

    public static JsonBodyMappingResult Apply(ReadOnlyMemory<byte> utf8, CompiledJsonBodyMapping mapping,
        int maxBytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (maxBytes is < 1024 or > 1048576) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        ArgumentNullException.ThrowIfNull(mapping);
        if (utf8.Length > maxBytes) return new(null, "transform_body_too_large", null);
        int? index = null;
        try
        {
            var data = utf8.Span;
            if (data.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) data = data[3..];
            ValidateJson(data, ct);
            var root = JsonNode.Parse(data, documentOptions: new JsonDocumentOptions { MaxDepth = 32 });
            byte[]? output = null;
            for (var i = 0; i < mapping.Operations.Count; i++)
            {
                ct.ThrowIfCancellationRequested(); index = i + 1;
                Execute(ref root, mapping.Operations[i]);
                output = Serialize(root, maxBytes, ct);
                ValidateJson(output, ct);
            }
            ct.ThrowIfCancellationRequested();
            return new(output!, null, null);
        }
        catch (BodyBudgetExceeded) { return new(null, "transform_body_too_large", index); }
        catch (OperationFailed) { return new(null, "transform_json_operation_failed", index); }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or InvalidOperationException)
        { return new(null, "transform_json_invalid", index); }
    }

    public static string? ValidateUtf8(ReadOnlyMemory<byte> utf8,CancellationToken ct)
    {
        try{var data=utf8.Span;if(data.StartsWith(new byte[]{0xef,0xbb,0xbf}))data=data[3..];ValidateJson(data,ct);return null;}
        catch(Exception error)when(error is JsonException or DecoderFallbackException or InvalidOperationException){return "transform_json_invalid";}
    }

    private static void ValidateJson(ReadOnlySpan<byte> data, CancellationToken ct)
    {
        StrictUtf8.GetCharCount(data);
        var reader = new Utf8JsonReader(data, new JsonReaderOptions { MaxDepth = 32 });
        var objects = new Stack<HashSet<string>>(); var any = false;
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested(); any = true;
            if (reader.TokenType == JsonTokenType.StartObject) objects.Push(new(StringComparer.Ordinal));
            else if (reader.TokenType == JsonTokenType.EndObject) objects.Pop();
            if (reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName)
            {
                var value = reader.GetString()!;
                for (var i = 0; i < value.Length; i++)
                {
                    if (!char.IsSurrogate(value[i])) continue;
                    if (!char.IsHighSurrogate(value[i]) || ++i >= value.Length || !char.IsLowSurrogate(value[i])) throw new JsonException();
                }
                if (reader.TokenType == JsonTokenType.PropertyName && !objects.Peek().Add(value)) throw new JsonException();
            }
        }
        if (!any) throw new JsonException();
    }

    private static void Execute(ref JsonNode? root, CompiledJsonBodyOperation op)
    {
        JsonNode? value = null;
        if (op.Op is "copy" or "move")
        {
            value = Read(root, op.From!).DeepCloneOrNull();
            if (op.Op == "move")
            {
                if (op.Path.SequenceEqual(op.From!, StringComparer.Ordinal)) return;
                Remove(ref root, op.From!);
            }
            Add(ref root, op.Path, value, replace: false); return;
        }
        if (op.Op == "remove") { Remove(ref root, op.Path); return; }
        if (op.Value is JsonElement constant) value = JsonNode.Parse(constant.GetRawText());
        Add(ref root, op.Path, value, replace: op.Op == "replace");
    }

    private static JsonNode? DeepCloneOrNull(this JsonNode? node) => node?.DeepClone();
    private static JsonNode? Read(JsonNode? root, IReadOnlyList<string> path)
    {
        var node = root;
        foreach (var token in path)
        {
            if (node is JsonObject obj)
            { if (!obj.TryGetPropertyValue(token, out node)) throw new OperationFailed(); }
            else if (node is JsonArray array) node = array[Index(token, array.Count, false)];
            else throw new OperationFailed();
        }
        return node;
    }
    private static void Add(ref JsonNode? root, IReadOnlyList<string> path, JsonNode? value, bool replace)
    {
        if (path.Count == 0) { root = value; return; }
        var parent = Read(root, path.Take(path.Count - 1).ToArray()); var token = path[^1];
        if (parent is JsonObject obj)
        { if (replace && !obj.ContainsKey(token)) throw new OperationFailed(); obj[token] = value; }
        else if (parent is JsonArray array)
        { var index = Index(token, array.Count, !replace); if (replace) array[index] = value; else array.Insert(index, value); }
        else throw new OperationFailed();
    }
    private static void Remove(ref JsonNode? root, IReadOnlyList<string> path)
    {
        if (path.Count == 0) throw new OperationFailed();
        var parent = Read(root, path.Take(path.Count - 1).ToArray()); var token = path[^1];
        if (parent is JsonObject obj) { if (!obj.Remove(token)) throw new OperationFailed(); }
        else if (parent is JsonArray array) array.RemoveAt(Index(token, array.Count, false));
        else throw new OperationFailed();
    }
    private static int Index(string token, int count, bool insert)
    {
        if (insert && token == "-") return count;
        if (token.Length == 0 || token.Length > 1 && token[0] == '0' || token.Any(c => c is < '0' or > '9')
            || !int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            || value > count || !insert && value == count) throw new OperationFailed();
        return value;
    }
    private static byte[] Serialize(JsonNode? root, int maxBytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var stream = new BoundedJsonStream(maxBytes);
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { MaxDepth = 32 }))
        { if (root is null) writer.WriteNullValue(); else root.WriteTo(writer, OutputOptions); }
        ct.ThrowIfCancellationRequested(); return stream.ToArray();
    }
    private sealed class OperationFailed : Exception;
    private sealed class BodyBudgetExceeded : Exception;
    private sealed class BoundedJsonStream(int limit) : MemoryStream
    {
        private void Check(int count) { if (Length + count > limit) throw new BodyBudgetExceeded(); }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
    }
}
