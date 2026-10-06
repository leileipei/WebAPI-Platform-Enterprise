using System.Text.Json.Nodes;
using WebApi.Contracts.Common;
namespace WebApi.Infrastructure.Comparisons.Rules;
public static class SecurityRequirementRules
{
    // Each object is an AND of available schemes/scopes; array entries form an OR.
    // Availability is an abstract shared domain, not a claim about token validation.
    public static JsonNode BuildPredicate(JsonNode? security)
    {
        if(security is null)return Object();
        if(security is not JsonArray alternatives)throw Invalid();
        if(alternatives.Count>100)throw Budget();
        if(alternatives.Count==0)return Object();
        var branches=new JsonArray();var entries=0;
        foreach(var entry in alternatives){
            if(entry is not JsonObject requirements)throw Invalid();
            var properties=new JsonObject();var required=new JsonArray();
            foreach(var(name,value)in requirements.OrderBy(x=>x.Key,StringComparer.Ordinal)){
                if(++entries>1000)throw Budget();
                if(string.IsNullOrWhiteSpace(name)||name.Length>256||value is not JsonArray scopes)throw Invalid();
                if(scopes.Count>100)throw Budget();
                var availability=new JsonObject{["present"]=new JsonObject{["const"]=true}};var availabilityRequired=new JsonArray("present");
                var scopeProperties=new JsonObject();var scopeRequired=new JsonArray();
                foreach(var scope in scopes){
                    if(++entries>1000)throw Budget();
                    if(scope is not JsonValue text||!text.TryGetValue<string>(out var key)||string.IsNullOrWhiteSpace(key)||key.Length>256)throw Invalid();
                    if(scopeProperties.ContainsKey(key))continue;
                    scopeProperties[key]=new JsonObject{["const"]=true};scopeRequired.Add(key);
                }
                if(scopeRequired.Count>0){availability["scopes"]=Object(scopeProperties,scopeRequired);availabilityRequired.Add("scopes");}
                properties[name]=Object(availability,availabilityRequired);required.Add(name);
            }
            branches.Add(Object(properties,required));
        }
        return branches.Count==1?branches[0]!.DeepClone():new JsonObject{["anyOf"]=branches};
    }
    private static JsonObject Object(JsonObject? properties=null,JsonArray? required=null)=>new(){["type"]="object",["properties"]=properties??new(),["required"]=required??new()};
    private static ApiException Invalid()=>new(422,"invalid_security_requirement","认证要求必须为 OR 数组、AND 对象及字符串 scope 数组。");
    private static ApiException Budget()=>new(422,"security_model_budget","认证组合模型超过数量或字段长度预算。");
}
