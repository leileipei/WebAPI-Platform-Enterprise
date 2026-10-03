using System.Text.Json;
using WebApi.Contracts.Security;
namespace WebApi.Contracts.Common;
public sealed record CommandIdentity(Guid ActorId,ScopeRef Scope,string Operation,string Key);
public sealed record CommandRequestContext(string IdempotencyKey);
public static class CanonicalJson
{
    public static readonly JsonSerializerOptions Options=new(JsonSerializerDefaults.Web);
    public static byte[] Serialize<T>(T value)
    {
        var element=JsonSerializer.SerializeToElement(value,Options);using var stream=new MemoryStream();using(var writer=new Utf8JsonWriter(stream)) Write(writer,element);return stream.ToArray();
    }
    private static void Write(Utf8JsonWriter writer,JsonElement element)
    {
        if(element.ValueKind==JsonValueKind.Object) {writer.WriteStartObject();foreach(var p in element.EnumerateObject().OrderBy(p=>p.Name,StringComparer.Ordinal)) {writer.WritePropertyName(p.Name);Write(writer,p.Value);}writer.WriteEndObject();}
        else if(element.ValueKind==JsonValueKind.Array) {writer.WriteStartArray();foreach(var item in element.EnumerateArray()) Write(writer,item);writer.WriteEndArray();}else element.WriteTo(writer);
    }
}
