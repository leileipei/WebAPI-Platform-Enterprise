using System.Text.Json;
using WebApi.Contracts.Common;
namespace WebApi.Infrastructure.Catalog;
public static class JsonFields
{
    public static void Validate(string? json,bool allowNull=true)
    {
        if(json is null) {if(!allowNull) throw new ApiException(422,"invalid_json","必须提供JSON。");return;}
        try {using var document=JsonDocument.Parse(json,new JsonDocumentOptions {MaxDepth=64});}
        catch(JsonException) {throw new ApiException(422,"invalid_json","JSON结构不合法。");}
    }
}
