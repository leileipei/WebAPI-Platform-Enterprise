using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebApi.Contracts.Runtime;

/// <summary>Preserves legacy bytes while representing a global governance credential's explicit null environment.</summary>
public sealed class RuntimeCredentialJsonConverter : JsonConverter<RuntimeCredential>
{
    private sealed record Fields(Guid Id, string AccessKey, string Hash, string Status, DateTimeOffset ValidFrom,
        DateTimeOffset ExpiresAt, Guid? EnvironmentId = null, string? BindingMode = null);

    public override RuntimeCredential? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var fields = JsonSerializer.Deserialize<Fields>(ref reader, options);
        return fields is null ? null : new(fields.Id, fields.AccessKey, fields.Hash, fields.Status, fields.ValidFrom,
            fields.ExpiresAt, fields.EnvironmentId, fields.BindingMode);
    }

    public override void Write(Utf8JsonWriter writer, RuntimeCredential value, JsonSerializerOptions options)
    {
        string Name(string property) => options.PropertyNamingPolicy?.ConvertName(property) ?? property;
        writer.WriteStartObject();
        writer.WriteString(Name(nameof(RuntimeCredential.Id)), value.Id);
        writer.WriteString(Name(nameof(RuntimeCredential.AccessKey)), value.AccessKey);
        writer.WriteString(Name(nameof(RuntimeCredential.Hash)), value.Hash);
        writer.WriteString(Name(nameof(RuntimeCredential.Status)), value.Status);
        writer.WriteString(Name(nameof(RuntimeCredential.ValidFrom)), value.ValidFrom);
        writer.WriteString(Name(nameof(RuntimeCredential.ExpiresAt)), value.ExpiresAt);
        if (value.EnvironmentId is Guid environmentId)
            writer.WriteString(Name(nameof(RuntimeCredential.EnvironmentId)), environmentId);
        else if (value.BindingMode == "LegacyGlobal")
            writer.WriteNull(Name(nameof(RuntimeCredential.EnvironmentId)));
        if (value.BindingMode is not null)
            writer.WriteString(Name(nameof(RuntimeCredential.BindingMode)), value.BindingMode);
        writer.WriteEndObject();
    }
}
