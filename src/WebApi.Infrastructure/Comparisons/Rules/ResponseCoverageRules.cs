using System.Globalization;
using System.Text.Json.Nodes;
namespace WebApi.Infrastructure.Comparisons.Rules;
internal static class ResponseCoverageRules
{
    internal static JsonNode? Effective(JsonObject responses,int status)
    {
        var exact=status.ToString(CultureInfo.InvariantCulture);if(responses.TryGetPropertyValue(exact,out var value))return value;
        var range=(status/100).ToString(CultureInfo.InvariantCulture)+"XX";
        foreach(var(key,node)in responses)if(string.Equals(key,range,StringComparison.OrdinalIgnoreCase))return node;
        return responses["default"];
    }
    // Partition the finite HTTP status domain, preserving exact > range > default.
    internal static IEnumerable<(int Status,JsonObject? Before,JsonObject? After)> Groups(OpenApiContractRules source,OpenApiContractRules.HttpOperation before,OpenApiContractRules target,OpenApiContractRules.HttpOperation after)
    {
        var seen=new HashSet<(JsonObject?,JsonObject?,string,string)>();
        for(var status=100;status<=599;status++){
            var a=source.EffectiveResponse(before,status);var b=target.EffectiveResponse(after,status);
            var fromManaged=source.ManagedResponseKey(status);var toManaged=target.ManagedResponseKey(status);
            if((b is not null||toManaged.Length>0)&&seen.Add((a,b,fromManaged,toManaged)))yield return(status,a,b);
        }
    }
}
