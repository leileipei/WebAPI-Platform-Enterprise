using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WebApi.Contracts.Common;
using WebApi.Contracts.CredentialUsage;
using WebApi.Domain.CredentialUsage;
using Xunit;
namespace WebApi.Domain.Tests;
public sealed class UsageBatchRulesTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-10-15T12:30:00Z");
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static Guid Id(int n)=>Guid.Parse($"{n}{n}{n}{n}{n}{n}{n}{n}-{n}{n}{n}{n}-{n}{n}{n}{n}-{n}{n}{n}{n}-{n}{n}{n}{n}{n}{n}{n}{n}{n}{n}{n}{n}");
    private static UsageBatchRequest Valid()
    {
        var hour=Now.AddMinutes(-30);var source=new UsageSource(Id(3),1,1,new string('a',64));
        var item=new UsageItem(Id(1),Id(2),Id(4),hour.AddHours(-1),hour.AddHours(2),source,hour,1,hour.AddSeconds(1),hour.AddSeconds(2));
        return new(Id(7),new("1.0",Id(5),Id(6),1,Now,Now,[new(hour,Now)],[],[item]));
    }
    private static byte[] Bytes(UsageBatchRequest value)=>JsonSerializer.SerializeToUtf8Bytes(value,Json);
    private static JsonObject Node()=>JsonSerializer.SerializeToNode(Valid(),Json)!.AsObject();
    private static ApiException Invalid(JsonObject node)=>Assert.Throws<ApiException>(()=>UsageBatchRules.Parse(Encoding.UTF8.GetBytes(node.ToJsonString()),Now,24));

    [Fact] public void Valid_batch_and_maximum_item_count_value_are_accepted()
    {
        var input=Valid();var parsed=UsageBatchRules.Parse(Bytes(input),Now,24);Assert.Equal(1,Assert.Single(parsed.Core.Items).Count);
        var max=input with {Core=input.Core with {Items=[input.Core.Items[0] with {Count=1000000}]}};
        Assert.Equal(1000000,Assert.Single(UsageBatchRules.Parse(Bytes(max),Now,24).Core.Items).Count);
    }
    [Theory]
    [InlineData("unknown")] [InlineData("nested-unknown")] [InlineData("duplicate-item")] [InlineData("empty-sender")]
    [InlineData("bad-protocol")] [InlineData("sequence-zero")] [InlineData("count-zero")] [InlineData("count-over")]
    [InlineData("count-fraction")] [InlineData("count-string")] [InlineData("hash-uppercase")] [InlineData("source-zero")]
    [InlineData("missing-nullable")] [InlineData("null-items")] [InlineData("wrong-hour")] [InlineData("reversed-observed")]
    [InlineData("expired-grant")] [InlineData("reversed-interval")] [InlineData("empty-gap-reason")]
    public void Malformed_or_out_of_window_batches_are_rejected(string kind)
    {
        var n=Node();var core=n["core"]!.AsObject();var item=core["items"]![0]!.AsObject();
        switch(kind)
        {
            case "unknown":n["environmentId"]=Id(8).ToString();break;
            case "nested-unknown":item["source"]!["unexpected"]=1;break;
            case "duplicate-item":core["items"]!.AsArray().Add(item.DeepClone());break;
            case "empty-sender":n["senderInstanceId"]=Guid.Empty.ToString();break;
            case "bad-protocol":core["protocol"]="2.0";break;
            case "sequence-zero":core["sequence"]=0;break;
            case "count-zero":item["count"]=0;break;
            case "count-over":item["count"]=1000001;break;
            case "count-fraction":item["count"]=1.5;break;
            case "count-string":item["count"]="1";break;
            case "hash-uppercase":item["source"]!["payloadHash"]=new string('A',64);break;
            case "source-zero":item["source"]!["configVersion"]=0;break;
            case "missing-nullable":item.Remove("grantExpiresAt");break;
            case "null-items":core["items"]=null;break;
            case "wrong-hour":item["hourStart"]=Now.ToString("O");break;
            case "reversed-observed":item["firstObservedAt"]=Now.ToString("O");break;
            case "expired-grant":item["grantExpiresAt"]=item["lastObservedAt"]!.DeepClone();break;
            case "reversed-interval":core["intervals"]![0]!["from"]=Now.AddSeconds(1).ToString("O");break;
            case "empty-gap-reason":core["gaps"]=new JsonArray(new JsonObject{["from"]=Now.AddSeconds(-1).ToString("O"),["to"]=Now.ToString("O"),["reason"]=""});break;
        }
        Assert.Equal(422,Invalid(n).Status);
    }
    [Fact] public void Duplicate_fields_at_every_depth_are_rejected()
    {
        var json=Encoding.UTF8.GetString(Bytes(Valid()));
        var variants=new[]{json.Replace("\"protocol\":\"1.0\"","\"protocol\":\"1.0\",\"protocol\":\"1.0\""),json.Replace("\"count\":1","\"count\":1,\"count\":1"),json.Replace("\"configVersion\":1","\"configVersion\":1,\"configVersion\":1")};
        foreach(var value in variants)Assert.Equal("invalid_usage_batch",Assert.Throws<ApiException>(()=>UsageBatchRules.Parse(Encoding.UTF8.GetBytes(value),Now,24)).Code);
    }
    [Fact] public void Byte_limit_is_exact_and_not_bypassed_by_trailing_whitespace()
    {
        var bytes=Bytes(Valid());var padded=new byte[256*1024];bytes.CopyTo(padded,0);Array.Fill(padded,(byte)' ',bytes.Length,padded.Length-bytes.Length);
        Assert.Single(UsageBatchRules.Parse(padded,Now,24).Core.Items);
        Assert.Equal(413,Assert.Throws<ApiException>(()=>UsageBatchRules.Parse((byte[])[..padded,(byte)' '],Now,24)).Status);
    }
    [Fact] public void Sender_change_preserves_hash_but_core_change_does_not()
    {
        var input=Valid();var core=input.Core;var hash=UsageBatchRules.CoreHash(core);
        Assert.Equal(hash,UsageBatchRules.CoreHash((input with {SenderInstanceId=Id(8)}).Core));
        Assert.NotEqual(hash,UsageBatchRules.CoreHash(core with {ProducerInstanceId=Id(8)}));
        Assert.NotEqual(hash,UsageBatchRules.CoreHash(core with {Items=[core.Items[0] with {Count=2}]}));
        Assert.Equal("4b825c950c551f15df122bed3f1b1fd0f02b0e2cfce529cbd717e3c0239e82f3",hash);
    }
    [Fact] public void Equivalent_time_offsets_and_item_order_produce_the_same_hash()
    {
        var core=Valid().Core;var other=core.Items[0] with {CredentialId=Id(8)};
        var a=core with {Items=[core.Items[0],other]};var shifted=a with {SealedAt=a.SealedAt.ToOffset(TimeSpan.FromHours(2)),DurableThrough=a.DurableThrough.ToOffset(TimeSpan.FromHours(2)),Intervals=a.Intervals.Select(x=>new UsageInterval(x.From.ToOffset(TimeSpan.FromHours(2)),x.To.ToOffset(TimeSpan.FromHours(2)))).ToArray(),Items=a.Items.Reverse().Select(x=>x with {HourStart=x.HourStart.ToOffset(TimeSpan.FromHours(2)),FirstObservedAt=x.FirstObservedAt.ToOffset(TimeSpan.FromHours(2)),LastObservedAt=x.LastObservedAt.ToOffset(TimeSpan.FromHours(2)),GrantValidFrom=x.GrantValidFrom.ToOffset(TimeSpan.FromHours(2)),GrantExpiresAt=x.GrantExpiresAt?.ToOffset(TimeSpan.FromHours(2))}).ToArray()};
        Assert.Equal(UsageBatchRules.CoreHash(a),UsageBatchRules.CoreHash(shifted));
    }
    [Fact] public void Idle_pulse_and_two_sources_are_valid_but_three_sources_are_not()
    {
        var input=Valid();Assert.Empty(UsageBatchRules.Parse(Bytes(input with {Core=input.Core with {Items=[]}}),Now,24).Core.Items);
        var second=input.Core.Items[0] with {CredentialId=Id(8),Source=input.Core.Items[0].Source with {ReleaseId=Id(8)}};
        var two=input with {Core=input.Core with {Items=[input.Core.Items[0],second]}};Assert.Equal(2,UsageBatchRules.Parse(Bytes(two),Now,24).Core.Items.Count);
        var third=second with {CredentialId=Id(9),Source=second.Source with {ReleaseId=Id(9)}};Assert.Equal(422,Assert.Throws<ApiException>(()=>UsageBatchRules.Parse(Bytes(two with {Core=two.Core with {Items=[..two.Core.Items,third]}}),Now,24)).Status);
    }
    [Fact] public void Observed_future_tolerance_and_late_window_do_not_silently_expand()
    {
        var input=Valid();var bytes=Bytes(input with {Core=input.Core with {Intervals=[],Items=[input.Core.Items[0] with {FirstObservedAt=Now,LastObservedAt=Now}]}});
        Assert.Single(UsageBatchRules.Parse(bytes,Now.AddHours(24),24).Core.Items);
        Assert.Equal(422,Assert.Throws<ApiException>(()=>UsageBatchRules.Parse(bytes,Now.AddHours(24).AddTicks(1),24)).Status);
        bytes=Bytes(Valid());Assert.Single(UsageBatchRules.Parse(bytes,Now.AddSeconds(-120),24).Core.Items);
        Assert.Equal(422,Assert.Throws<ApiException>(()=>UsageBatchRules.Parse(bytes,Now.AddSeconds(-121),24)).Status);
    }
}
