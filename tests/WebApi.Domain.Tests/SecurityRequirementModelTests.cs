using System.Text.Json.Nodes;
using WebApi.Contracts.Catalog;
using WebApi.Infrastructure.Comparisons.Rules;
using WebApi.Infrastructure.Contracts;
using Xunit;
namespace WebApi.Domain.Tests;
public sealed class SecurityRequirementModelTests
{
    private static bool Accept(string requirements,string availability)
    {
        var schema=SecurityRequirementRules.BuildPredicate(JsonNode.Parse(requirements));var pack=CompatibilityProofTests.Pack(schema.ToJsonString());
        var result=new SchemaEvaluator().Evaluate(new(pack.Bundle,pack.Schema,JsonNode.Parse(availability),"request","Annotation",true,pack.Bundle.RootUri,"/components/schemas/Value"),new(),default);
        Assert.NotEqual("Incomplete",result.Status);return result.Status=="Valid";
    }
    [Theory][InlineData("null")][InlineData("[]")][InlineData("[{}]")]
    public void AnonymousAccessDoesNotRequireAvailability(string security)=>Assert.True(Accept(security,"{}"));
    [Fact] public void OrAndAndScopesArePositiveFullPredicates()
    {
        const string key="{\"key\":{\"present\":true}}";
        Assert.True(Accept("[{\"key\":[]},{\"other\":[]}]",key));Assert.False(Accept("[{\"key\":[],\"other\":[]}]",key));
        Assert.True(Accept("[{\"key\":[],\"other\":[]}]","{\"key\":{\"present\":true},\"other\":{\"present\":true}}"));
        Assert.False(Accept("[{\"oauth\":[\"read\",\"write\"]}]","{\"oauth\":{\"present\":true,\"scopes\":{\"read\":true}}}"));
        Assert.True(Accept("[{\"oauth\":[\"read\",\"write\"]}]","{\"oauth\":{\"present\":true,\"scopes\":{\"read\":true,\"write\":true}}}"));
        Assert.False(Accept("[{\"key\":[]}]","{\"key\":{\"present\":false}}"));Assert.False(Accept("[{\"oauth\":[\"read\"]}]","{\"oauth\":{\"present\":true,\"scopes\":{\"read\":false}}}"));
    }
    [Theory][InlineData("true")][InlineData("[1]")][InlineData("[{\"key\":true}]")][InlineData("[{\"oauth\":[1]}]")]
    public void InvalidRequirementShapesAreRejected(string security)=>Assert.Throws<WebApi.Contracts.Common.ApiException>(()=>SecurityRequirementRules.BuildPredicate(JsonNode.Parse(security)));
    [Fact] public void RequirementModelIsBounded()
    {
        var clauses=new JsonArray();for(var i=0;i<101;i++)clauses.Add(new JsonObject{["key"+i]=new JsonArray()});
        var error=Assert.Throws<WebApi.Contracts.Common.ApiException>(()=>SecurityRequirementRules.BuildPredicate(clauses));Assert.Equal("security_model_budget",error.Code);
    }

}
