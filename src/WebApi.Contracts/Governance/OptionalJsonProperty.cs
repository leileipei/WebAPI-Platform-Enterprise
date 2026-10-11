using System.Text.Json;
using System.Text.Json.Serialization;
namespace WebApi.Contracts.Governance;
[JsonConverter(typeof(OptionalJsonPropertyConverterFactory))]
public readonly record struct OptionalJsonProperty<T>(bool IsSpecified,T? Value);
public sealed class OptionalJsonPropertyConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type type)=>type.IsGenericType&&type.GetGenericTypeDefinition()==typeof(OptionalJsonProperty<>);
    public override JsonConverter CreateConverter(Type type,JsonSerializerOptions options)=>(JsonConverter)Activator.CreateInstance(typeof(OptionalJsonPropertyConverter<>).MakeGenericType(type.GetGenericArguments()[0]))!;
    private sealed class OptionalJsonPropertyConverter<T> : JsonConverter<OptionalJsonProperty<T>>
    {
        public override bool HandleNull=>true;
        public override OptionalJsonProperty<T> Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options)=>new(true,JsonSerializer.Deserialize<T>(ref reader,options));
        public override void Write(Utf8JsonWriter writer,OptionalJsonProperty<T> value,JsonSerializerOptions options)=>JsonSerializer.Serialize(writer,value.Value,options);
    }
}
