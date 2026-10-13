using Microsoft.AspNetCore.Http;
using WebApi.Gateway.Policies;
using WebApi.Contracts.Runtime;
using WebApi.Domain.Policies;
using WebApi.Gateway.Configuration;
using Xunit;

namespace WebApi.Gateway.Tests;

public sealed class CorsPolicyEvaluatorTests
{
    [Theory]
    [InlineData("GET",null,null,true,false,"no_origin")]
    [InlineData("GET","https://portal.example.com",null,true,false,null)]
    [InlineData("OPTIONS","https://portal.example.com","GET",true,true,null)]
    [InlineData("GET","https://evil.example.com",null,false,false,"cors_origin_rejected")]
    [InlineData("OPTIONS","https://portal.example.com","GET,POST",false,true,"cors_request_invalid")]
    [InlineData("POST","https://portal.example.com",null,false,false,"cors_method_rejected")]
    public void Cors_PureSelection_UsesOriginAndOriginalMethod(string method,string? origin,string? requested,bool allowed,bool preflight,string? reason)
    {
        var ctx=new DefaultHttpContext();ctx.Request.Method=method;if(origin is not null)ctx.Request.Headers.Origin=origin;if(requested is not null)ctx.Request.Headers["Access-Control-Request-Method"]=requested;
        var route=new RuntimeRoute(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),"/orders",["GET"],0,30000,true);var configuration=PolicyConfigurationValidator.ParseCors(CorsPipelineTests.Config(methods:["GET"]));
        var result=CorsPolicyEvaluator.Evaluate(ctx.Request,route,configuration);
        Assert.Equal(allowed,result.Allowed);Assert.Equal(preflight,result.Preflight);Assert.Equal(reason,result.Reason);
    }
    [Theory]
    [InlineData("origin-multi")][InlineData("method-multi")][InlineData("quoted-method")][InlineData("invalid-origin")][InlineData("empty-headers")][InlineData("many-headers")]
    public void Cors_MalformedFields_AreControlled(string kind)
    {
        var ctx=new DefaultHttpContext();ctx.Request.Method="OPTIONS";ctx.Request.Headers.Origin="https://portal.example.com";ctx.Request.Headers["Access-Control-Request-Method"]="GET";
        if(kind=="origin-multi")ctx.Request.Headers.Origin=new Microsoft.Extensions.Primitives.StringValues(["https://portal.example.com","https://other.example.com"]);
        if(kind=="method-multi")ctx.Request.Headers["Access-Control-Request-Method"]=new Microsoft.Extensions.Primitives.StringValues(["GET","POST"]);
        if(kind=="quoted-method")ctx.Request.Headers["Access-Control-Request-Method"]="\"GET\"";
        if(kind=="invalid-origin")ctx.Request.Headers.Origin="null";
        if(kind=="empty-headers")ctx.Request.Headers["Access-Control-Request-Headers"]="";
        if(kind=="many-headers")ctx.Request.Headers["Access-Control-Request-Headers"]=string.Join(",",Enumerable.Range(0,65).Select(i=>"x"+i));
        var route=new RuntimeRoute(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),"/orders",["GET"],0,30000,true);
        var result=CorsPolicyEvaluator.Evaluate(ctx.Request,route,PolicyConfigurationValidator.ParseCors(CorsPipelineTests.Config()));Assert.False(result.Allowed);Assert.Equal("cors_request_invalid",result.Reason);
    }

}
