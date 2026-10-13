using WebApi.Gateway.Policies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using WebApi.Domain.Policies;
using WebApi.Gateway.Configuration;
using Xunit;

namespace WebApi.Gateway.Tests;
public sealed class TransformPlannerTests
{
    private static TransformPlanResult Plan(HttpRequest request,string config)=>TransformPlanner.Plan(request,PolicyConfigurationValidator.ParseTransform(config));
    [Fact]
    public void Transform_PathAndOrderedCaseSensitiveQuery_LeaveOriginalRequest()
    {
        var ctx=new DefaultHttpContext();ctx.Request.Path="/gateway/stock";ctx.Request.QueryString=new("?sku=A&sku=B&debug=1&Region=US");
        var result=Plan(ctx.Request,TransformPipelineTests.Config("/gateway","/internal/v2",true));Assert.Null(result.ErrorStatus);var plan=result.Plan!;
        Assert.Equal("/internal/v2/stock",plan.Path.Value);Assert.Equal("?sku=A&sku=B&Region=US&region=cn",plan.Query.Value);Assert.Equal("/gateway/stock",ctx.Request.Path);Assert.Equal("?sku=A&sku=B&debug=1&Region=US",ctx.Request.QueryString.Value);
    }
    [Theory]
    [InlineData("/gatewayx/stock","/gatewayx/stock","transform_path_mismatch")]
    [InlineData("/gateway/x","/gateway/%2Fx","transform_path_invalid")]
    [InlineData("/gateway/x","/gateway/%5Cx","transform_path_invalid")]
    [InlineData("/gateway/x","/gateway/%2ex","transform_path_invalid")]
    [InlineData("/gateway/x","/gateway/%25x","transform_path_invalid")]
    [InlineData("/gateway/x","/gateway/%GG","transform_path_invalid")]
    [InlineData("/gateway/../x","/gateway/../x","transform_path_invalid")]
    [InlineData("/gateway//x","/gateway//x","transform_path_invalid")]
    [InlineData("/gateway/\\x","/gateway/\\x","transform_path_invalid")]
    public void Transform_PathRejectsAmbiguityAndPartialPrefix(string path,string raw,string reason)
    {var ctx=new DefaultHttpContext();ctx.Request.Path=path;ctx.Features.Get<IHttpRequestFeature>()!.RawTarget=raw;var result=Plan(ctx.Request,TransformPipelineTests.Config("/gateway","/internal"));Assert.Equal(422,result.ErrorStatus);Assert.Equal(reason,result.Reason);}
    [Theory][InlineData(16384,true)][InlineData(16385,false)]
    public void Transform_PathQueryBudget_IsExact(int length,bool success)
    {var ctx=new DefaultHttpContext();ctx.Request.Path="/x";ctx.Request.QueryString=new("?v="+new string('a',length-5));var result=Plan(ctx.Request,TransformPipelineTests.Config());Assert.Equal(success?null:414,result.ErrorStatus);Assert.Equal(success?null:"transform_budget_exceeded",result.Reason);}
    [Theory][InlineData("/gateway","/internal/")][InlineData("/gateway/x","/internal/x")]
    public void Transform_ExactPrefixRoot_HasSingleJoiningSlash(string path,string expected)
    {var ctx=new DefaultHttpContext();ctx.Request.Path=path;var result=Plan(ctx.Request,TransformPipelineTests.Config("/gateway","/internal"));Assert.Equal(expected,result.Plan!.Path.Value);}
}
