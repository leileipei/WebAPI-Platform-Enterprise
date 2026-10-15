using System.Text.Json;
using System.Text.Json.Serialization;
namespace WebApi.Contracts.CredentialUsage;

public static class UsageProtocolJson
{
    public static JsonSerializerOptions Options {get;}=Create();
    private static JsonSerializerOptions Create()
    {
        var options=new JsonSerializerOptions(JsonSerializerDefaults.Web){PropertyNameCaseInsensitive=false,NumberHandling=JsonNumberHandling.Strict,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,RespectRequiredConstructorParameters=true,MaxDepth=8};
        options.Converters.Add(new UtcTimestampConverter());return options;
    }
    private sealed class UtcTimestampConverter:JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader,Type typeToConvert,JsonSerializerOptions options)
        {
            if(reader.TokenType!=JsonTokenType.String||!reader.TryGetDateTimeOffset(out var value))throw new JsonException();
            var text=reader.GetString()!;var z=text.EndsWith('Z');
            if(!z&&(text.Length<6||text[^3]!=':'||text[^6] is not ('+' or '-')))throw new JsonException();
            var dot=text.IndexOf('.');var end=text.Length-(z?1:6);if(dot>=0&&end-dot-1>7)throw new JsonException();
            return value.ToUniversalTime();
        }
        public override void Write(Utf8JsonWriter writer,DateTimeOffset value,JsonSerializerOptions options)=>writer.WriteStringValue(value.UtcDateTime);
    }
}
