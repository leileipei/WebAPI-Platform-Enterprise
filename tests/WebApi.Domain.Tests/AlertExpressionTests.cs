using WebApi.Domain.Alerts;
using Xunit;
namespace WebApi.Domain.Tests;
public sealed class AlertExpressionTests
{
    [Theory]
    [InlineData(">",false)] [InlineData(">=",true)] [InlineData("<",false)]
    [InlineData("<=",true)] [InlineData("==",true)] [InlineData("!=",false)]
    public void OperatorsAndFiniteThresholdsAreAccepted(string op,bool equal)
    {
        var expression=AlertExpressionParser.Parse("error_5xx_ratio",$"error_5xx_ratio {op} 0.05");
        Assert.Equal(equal,AlertExpressionParser.Compare(expression,0.05));
        Assert.Null(AlertExpressionParser.Compare(expression,null));
        Assert.Null(AlertExpressionParser.Compare(expression,double.NaN));
        Assert.Null(AlertExpressionParser.Compare(expression,double.PositiveInfinity));
    }
    [Theory]
    [InlineData("sum(rate(secret[5m])) > 0")] [InlineData("error_5xx_ratio > NaN")]
    [InlineData("error_5xx_ratio > Infinity")] [InlineData("error_5xx_ratio > 1.01")]
    [InlineData("error_5xx_ratio > -0.1")] [InlineData("request_rps > 1")]
    [InlineData("error_5xx_ratio > 1 or 1")] [InlineData("error_5xx_ratio>0.1")]
    public void RawPromQlAndNaNRejected(string value)=>Assert.Throws<ArgumentException>(()=>AlertExpressionParser.Parse("error_5xx_ratio",value));
    [Theory]
    [InlineData("request_rps")] [InlineData("latency_p95_ms")] [InlineData("unhealthy_destinations")]
    public void NonNegativeMetricsAndScientificNotation(string metric)
    {
        Assert.Equal(1000,AlertExpressionParser.Parse(metric,$"{metric} > 1e3").Threshold);
        Assert.Throws<ArgumentException>(()=>AlertExpressionParser.Parse(metric,$"{metric} > -1"));
        Assert.Throws<ArgumentException>(()=>AlertExpressionParser.Parse(metric,$"{metric} > 1e400"));
        Assert.Throws<ArgumentException>(()=>AlertExpressionParser.Parse("circuit_breaker","circuit_breaker > 0"));
    }
}
