using System.Text.Json;
using Xunit;
namespace WebApi.EndToEnd.Tests;
public sealed class SystemSettingsLoopTests
{
    [Fact,Trait("Scenario","Settings")] public async Task SettingsChangeRequiresNormalPublishToReachBothGateways()
    {
        var directory=System.Environment.GetEnvironmentVariable("WEBAPI_SETTINGS_RESULT_DIRECTORY");Assert.NotNull(directory);
        var result=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory!,"result.json"))).RootElement;
        foreach(var name in new[]{"settings-persistence","snapshot-unchanged-before-publish","both-gateways-ack","old-route-still-works","audit-export-disabled"})
        {var check=result.GetProperty("checks").EnumerateArray().Single(c=>c.GetProperty("name").GetString()==name);Assert.True(check.GetProperty("pass").GetBoolean(),name);Assert.True(check.GetProperty("evidence").EnumerateObject().Any());}
        Assert.Equal(2,result.GetProperty("finalRelease").GetProperty("targets").EnumerateArray().Count(t=>t.GetProperty("acknowledged").GetBoolean()));
        foreach(var response in result.GetProperty("requests").EnumerateArray())Assert.Equal(200,response.GetProperty("status").GetInt32());
    }
}
