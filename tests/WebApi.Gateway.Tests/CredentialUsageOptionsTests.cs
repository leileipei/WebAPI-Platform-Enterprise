using Microsoft.Extensions.Configuration;
using WebApi.Gateway.CredentialUsage;
using WebApi.Infrastructure.CredentialUsage;
using Xunit;
namespace WebApi.Gateway.Tests;
public sealed class CredentialUsageOptionsTests
{
    private static IConfiguration Configuration(string key,string value)=>new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"CredentialUsage:"+key,value}}).Build();
    [Fact] public void Defaults_enable_bounded_collection_and_leave_replay_retention_margin()
    {
        var config=new ConfigurationBuilder().Build();var options=CredentialUsageOptions.Read(config);var retention=CredentialUsageRetentionOptions.Read(config);
        Assert.True(options.Enabled);Assert.Equal(15,options.FlushIntervalSeconds);Assert.Equal(1000,options.CheckpointIntervalMs);
        Assert.Equal(10000,options.MaxInMemoryKeys);Assert.Equal(64*1024*1024,options.MaxPendingBytes);Assert.Equal(1000,options.MaxBatchEntries);Assert.Equal(256*1024,options.MaxBatchBytes);Assert.Equal(24,options.MaxPendingAgeHours);
        Assert.Equal(30,retention.RetentionDays);Assert.Equal(25,retention.BatchHashHours);Assert.Equal(2,retention.IngestConcurrency);Assert.Equal(10,retention.IngestTimeoutSeconds);
    }
    [Theory]
    [InlineData("FlushIntervalSeconds","4")] [InlineData("FlushIntervalSeconds","61")]
    [InlineData("CheckpointIntervalMs","99")] [InlineData("CheckpointIntervalMs","5001")]
    [InlineData("MaxInMemoryKeys","99")] [InlineData("MaxInMemoryKeys","20001")]
    [InlineData("MaxPendingBytes","8388607")] [InlineData("MaxPendingBytes","134217729")]
    [InlineData("MaxBatchEntries","0")] [InlineData("MaxBatchEntries","1001")]
    [InlineData("MaxBatchBytes","16383")] [InlineData("MaxBatchBytes","262145")]
    [InlineData("MaxPendingAgeHours","0")] [InlineData("MaxPendingAgeHours","25")]
    [InlineData("Enabled","maybe")]
    public void Invalid_gateway_deployment_values_fail_startup(string key,string value)=>Assert.Throws<InvalidOperationException>(()=>CredentialUsageOptions.Read(Configuration(key,value)));
    [Theory]
    [InlineData("RetentionDays","0")] [InlineData("RetentionDays","91")]
    [InlineData("BatchHashHours","24")] [InlineData("BatchHashHours","169")]
    [InlineData("IngestConcurrency","0")] [InlineData("IngestConcurrency","5")]
    [InlineData("IngestTimeoutSeconds","0")] [InlineData("IngestTimeoutSeconds","11")]
    public void Invalid_retention_or_processing_values_fail_startup(string key,string value)=>Assert.Throws<InvalidOperationException>(()=>CredentialUsageRetentionOptions.Read(Configuration(key,value)));
    [Fact] public void Shorter_queue_window_still_requires_one_hour_hash_margin()
    {
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"CredentialUsage:MaxPendingAgeHours","1"},{"CredentialUsage:BatchHashHours","2"},{"CredentialUsage:Enabled","false"}}).Build();
        Assert.False(CredentialUsageOptions.Read(config).Enabled);Assert.Equal(2,CredentialUsageRetentionOptions.Read(config).BatchHashHours);
    }
}
