using System.Text.Json;
using WebApi.Contracts.Common;
using WebApi.Contracts.Policies;

namespace WebApi.ControlPlane.Policies;

public static class TransformPreviewRequestReader
{
    public static async Task<TransformPreviewRequest> ReadAsync(HttpRequest request, int maxBodyBytes, CancellationToken ct)
    {
        if (maxBodyBytes is < 1024 or > 1048576) throw new ArgumentOutOfRangeException(nameof(maxBodyBytes));
        if (!request.HasJsonContentType()) throw new ApiException(415, "invalid_request", "预览请求必须为JSON。");
        var maximum = 6 * maxBodyBytes + 65536;
        if (request.ContentLength > maximum) throw Large();
        using var buffer = new MemoryStream(); var block = new byte[8192];
        while (true)
        {
            var count = await request.Body.ReadAsync(block.AsMemory(0, (int)Math.Min(block.Length, maximum - buffer.Length + 1)), ct);
            if (count == 0) break;
            if (buffer.Length + count > maximum) throw Large();
            buffer.Write(block, 0, count);
        }
        ct.ThrowIfCancellationRequested();
        try
        {
            using var json = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), new JsonDocumentOptions { MaxDepth = 16 });
            var root = json.RootElement; Unique(root);
            Fields(root, ["scope", "config", "direction", "sampleJson"]);
            var scope = root.GetProperty("scope");
            if (scope.ValueKind != JsonValueKind.Object || !scope.TryGetProperty("organizationId", out var organization)
                || organization.ValueKind != JsonValueKind.String || !organization.TryGetGuid(out var org) || org == Guid.Empty
                || scope.EnumerateObject().Any(p => p.Name is not ("organizationId" or "projectId"))) throw Invalid();
            Guid? project = null;
            if (scope.TryGetProperty("projectId", out var value) && value.ValueKind != JsonValueKind.Null)
            { if (value.ValueKind != JsonValueKind.String || !value.TryGetGuid(out var id) || id == Guid.Empty) throw Invalid(); project = id; }
            var config = Text(root, "config"); var direction = Text(root, "direction"); var sample = Text(root, "sampleJson");
            if (direction is not ("Request" or "Response")) throw Invalid();
            return new(new(org, project), config, direction, sample);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        { throw Invalid(); }
    }
    private static string Text(JsonElement root, string field)
    { var value = root.GetProperty(field); if (value.ValueKind != JsonValueKind.String) throw Invalid(); return value.GetString()!; }
    private static void Fields(JsonElement root, string[] expected)
    { if (root.ValueKind != JsonValueKind.Object || !root.EnumerateObject().Select(p=>p.Name).ToHashSet(StringComparer.Ordinal).SetEquals(expected)) throw Invalid(); }
    private static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        { var names = new HashSet<string>(StringComparer.Ordinal); foreach (var property in value.EnumerateObject()) { if (!names.Add(property.Name)) throw Invalid(); Unique(property.Value); } }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Unique(item);
    }
    private static ApiException Invalid() => new(422, "invalid_request", "预览请求缺少必填字段、作用域或类型不合法。");
    private static ApiException Large() => new(413, "invalid_request", "预览请求超过字节预算。");
}
