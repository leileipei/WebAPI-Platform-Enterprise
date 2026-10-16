using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Common;
using WebApi.Contracts.CredentialUsage;
using WebApi.Contracts.Runtime;
using WebApi.Infrastructure.Gateway;
using WebApi.Infrastructure.CredentialUsage;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;

// Real PostgreSQL and HTTP. A missing endpoint/service fails these assertions before implementation.
public sealed class CredentialUsageIngestTests
{
    private sealed class SlowEofContent(byte[] payload):HttpContent
    {
        protected override bool TryComputeLength(out long length){length=0;return false;}
        protected override async Task SerializeToStreamAsync(Stream stream,TransportContext? context)
        {await stream.WriteAsync(payload);await stream.FlushAsync();await Task.Delay(1500);}
    }
    private sealed class Capture : DbCommandInterceptor
    {
        public int SnapshotQueries;
        public TaskCompletionSource<bool>? NodeLock;
        public TaskCompletionSource<bool>? TotalsRead;
        public TaskCompletionSource<bool>? ContinueRead;
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,CommandExecutedEventData data,DbDataReader result,CancellationToken ct=default)
        {
            if(command.CommandText.Contains("SUM(count::numeric)",StringComparison.Ordinal)&&TotalsRead is {} reached&&ContinueRead is {} proceed)
            {TotalsRead=null;reached.TrySetResult(true);await proceed.Task.WaitAsync(ct);}
            return result;
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<DbDataReader> result,CancellationToken ct=default)
        {
            if(command.CommandText.Contains("gateway_config_snapshots",StringComparison.Ordinal))Interlocked.Increment(ref SnapshotQueries);
            if(command.CommandText.Contains("gateway_nodes",StringComparison.Ordinal)&&command.CommandText.Contains("FOR UPDATE",StringComparison.Ordinal))NodeLock?.TrySetResult(true);
            return ValueTask.FromResult(result);
        }
    }
    private sealed class Scenario : IAsyncDisposable
    {
        public ApiFixture Api {get;}=new();
        public Capture Sql {get;}=new();
        public GatewayNode Node {get;private set;}=null!;
        public ApplicationCredential Credential {get;private set;}=null!;
        public Guid Collector {get;}=Guid.NewGuid();
        public Guid Instance {get;set;}=Guid.NewGuid();
        public string Secret {get;}=new('s',48);
        public UsageSource Source {get;private set;}=null!;
        public byte[] Payload {get;private set;}=[];
        public DateTimeOffset Observed {get;init;}=new((DateTimeOffset.UtcNow.AddMinutes(-5).UtcTicks/10)*10,TimeSpan.Zero);
        public DateTimeOffset Hour=>new(Observed.Year,Observed.Month,Observed.Day,Observed.Hour,0,0,TimeSpan.Zero);
        public async Task InitializeAsync(int pendingAgeHours=24,int ingestTimeoutSeconds=10)
        {
            await Api.InitializeAsync(b=>{b.Configuration["CredentialUsage:MaxPendingAgeHours"]=pendingAgeHours.ToString(System.Globalization.CultureInfo.InvariantCulture);b.Configuration["CredentialUsage:IngestTimeoutSeconds"]=ingestTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);b.Services.AddDbContext<WebApiDbContext>(o=>o.AddInterceptors(Sql));});await Api.SeedConsumerAsync();
            Credential=new(){ApplicationId=Api.Application.Id,AccessKey="usage-test-key",SecretHash=new string('a',64),SecretLast4="aaaa",ValidFrom=Observed.AddDays(-1),ExpiresAt=Observed.AddDays(1)};
            Node=new(){EnvironmentId=Api.Environment.Id,NodeName="usage-ingest",InstanceId=Instance.ToString(),IdentityHash=NodeIdentityService.Hash(Secret),Enabled=true};
            var snapshot=new RuntimeSnapshot("2.0",Api.Environment.Id,1,Observed.AddMinutes(-2),
                [new(Guid.NewGuid(),Api.Api.Id,Api.Version.Id,Api.Cluster.Id,"/orders",["GET"],0,30000,true)],
                [new(Api.Cluster.Id,Api.Environment.Id,"RoundRobin",false,"/health",30,[new(Api.Destination.Id,"http://fixture:8080/",1)])],[],
                [new(Api.Application.Id,"Active",[new(Credential.Id,Credential.AccessKey,Credential.SecretHash,"Active",Credential.ValidFrom,Credential.ExpiresAt)],[new(Api.Api.Id,Observed.AddHours(-1),Observed.AddHours(1))])]);
            Payload=CanonicalJson.Serialize(snapshot);var hash=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Payload));
            var version=new GatewayConfigVersion{EnvironmentId=Api.Environment.Id,VersionNo=1,CreatedBy=Api.User.Id,SnapshotHash=hash,PublishedAt=Observed.AddMinutes(-1)};
            var release=new ReleaseRecord{EnvironmentId=Api.Environment.Id,ReleaseNo="USAGE-1",RequestedBy=Api.User.Id,ReleaseType="publish",Status="Succeeded",FromConfigVersion=0,ToConfigVersion=1,DeploymentSequence=1};
            Source=new(release.Id,1,1,hash);
            await using var db=Api.Context();db.AddRange(Credential,Node,version,release,new GatewayConfigSnapshot{ConfigVersionId=version.Id,Payload=Encoding.UTF8.GetString(Payload),PayloadBytes=Payload,SizeBytes=Payload.Length},new GatewayNodeEvent{GatewayNodeId=Node.Id,EventType="registered",Message="fixture registration",Detail=JsonSerializer.Serialize(new{instanceId=Instance})});await db.SaveChangesAsync();
            await db.Entry(Api.Application).ReloadAsync();
        }
        public UsageBatchRequest Batch(long sequence=1,long count=7)=>new(Instance,new("1.0",Instance,Collector,sequence,Observed.AddSeconds(2),Observed.AddSeconds(1),[new(Observed.AddSeconds(-1),Observed.AddSeconds(1))],[],[new(Api.Application.Id,Credential.Id,Api.Api.Id,Observed.AddHours(-1),Observed.AddHours(1),Source,Hour,count,Observed,Observed)]));
        public async Task<HttpResponseMessage> SendAsync(string suffix,object body,Guid? node=null,string? secret=null)
        {
            using var request=new HttpRequestMessage(HttpMethod.Post,$"/internal/v1/nodes/{node??Node.Id}/credential-usage/{suffix}"){Content=JsonContent.Create(body,options:UsageProtocolJson.Options)};
            request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",secret??Secret);return await Api.Client.SendAsync(request);
        }
        public async Task<UsageReceipt> BindAsync()
        {
            using var response=await SendAsync("collectors",new UsageCollectorRequest("1.0",Instance,Collector));
            Assert.Equal(HttpStatusCode.OK,response.StatusCode);return (await response.Content.ReadFromJsonAsync<UsageReceipt>())!;
        }
        public async Task<UsageReceipt> ApplyAsync(UsageBatchRequest? batch=null)
        {
            using var response=await SendAsync("batches",batch??Batch());Assert.Equal(HttpStatusCode.OK,response.StatusCode);return (await response.Content.ReadFromJsonAsync<UsageReceipt>())!;
        }
        public async Task AssertCountAsync(long count)
        {
            await using var db=Api.Context();var buckets=await db.Set<CredentialUsageHourly>().AsNoTracking().ToArrayAsync();Assert.Single(buckets);Assert.Equal(count,buckets[0].Count);
        }
        public ValueTask DisposeAsync()=>Api.DisposeAsync();
    }
    [Fact]
    public async Task Query_cannot_mix_old_zero_with_coverage_committed_by_concurrent_final_batch()
    {
        var now=DateTimeOffset.UtcNow;var hour=new DateTimeOffset(now.Year,now.Month,now.Day,now.Hour,0,0,TimeSpan.Zero).AddHours(-2);
        await using var s=new Scenario{Observed=hour.AddMinutes(10)};await s.InitializeAsync();await s.BindAsync();
        await using(var db=s.Api.Context())
        {
            await db.Set<GatewayNodeEvent>().Where(x=>x.GatewayNodeId==s.Node.Id&&x.EventType=="registered").ExecuteUpdateAsync(x=>x.SetProperty(y=>y.CreatedAt,hour.AddMinutes(-1)));
            await db.Set<CredentialUsageCollectorInstance>().Where(x=>x.NodeId==s.Node.Id).ExecuteUpdateAsync(x=>x.SetProperty(y=>y.RegisteredAt,hour.AddMinutes(-1)));
        }
        using var login=await s.Api.LoginAsync();login.EnsureSuccessStatusCode();
        var path=$"/api/v1/applications/{s.Api.Application.Id}/credential-usage?from={Uri.EscapeDataString(hour.ToString("O"))}&to={Uri.EscapeDataString(hour.AddHours(1).ToString("O"))}";
        s.Sql.TotalsRead=new(TaskCreationOptions.RunContinuationsAsynchronously);var reached=s.Sql.TotalsRead;
        s.Sql.ContinueRead=new(TaskCreationOptions.RunContinuationsAsynchronously);var proceed=s.Sql.ContinueRead;
        var pending=s.Api.Client.GetFromJsonAsync<JsonElement>(path);
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var batch=s.Batch(count:1);await s.ApplyAsync(batch with{Core=batch.Core with{Intervals=[new(hour,hour.AddHours(1))],DurableThrough=hour.AddHours(1),SealedAt=hour.AddHours(1).AddSeconds(1)}});
        }
        finally{proceed.TrySetResult(true);}
        var before=(await pending.WaitAsync(TimeSpan.FromSeconds(15))).GetProperty("page").GetProperty("items")[0];
        Assert.Equal("0",before.GetProperty("count").GetString());
        Assert.Equal("Partial",before.GetProperty("coverage").GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null,before.GetProperty("visibleLastUsedAt").ValueKind);
        var after=(await s.Api.Client.GetFromJsonAsync<JsonElement>(path)).GetProperty("page").GetProperty("items")[0];
        Assert.Equal("1",after.GetProperty("count").GetString());Assert.Equal("Available",after.GetProperty("coverage").GetProperty("state").GetString());
    }
    [Fact]
    public async Task Same_batch_replay_once_and_hash_conflict_rolls_back()
    {
        await using var s=new Scenario();await s.InitializeAsync();Assert.Equal("Registered",(await s.BindAsync()).Result);
        var first=await s.ApplyAsync();Assert.Equal("Applied",first.Result);Assert.Equal(2,first.NextExpectedSequence);
        Assert.Equal("AlreadyApplied",(await s.ApplyAsync()).Result);await s.AssertCountAsync(7);
        using var conflict=await s.SendAsync("batches",s.Batch(count:8));Assert.Equal(HttpStatusCode.Conflict,conflict.StatusCode);await s.AssertCountAsync(7);
        await using var db=s.Api.Context();var credential=await db.Set<ApplicationCredential>().SingleAsync(x=>x.Id==s.Credential.Id);var app=await db.Set<ApplicationRecord>().SingleAsync(x=>x.Id==s.Api.Application.Id);
        Assert.Equal(s.Observed,credential.LastUsedAt);Assert.Equal(s.Credential.Revision,credential.Revision);Assert.Equal(s.Api.Application.UpdatedAt,app.UpdatedAt);Assert.Equal(s.Api.Application.Revision,app.Revision);
        Assert.Equal(s.Payload,(await db.Set<GatewayConfigSnapshot>().SingleAsync()).PayloadBytes);Assert.Single(await db.Set<CredentialUsageBatch>().ToArrayAsync());
    }
    [Fact]
    public async Task Concurrent_same_batch_applies_once()
    {
        await using var s=new Scenario();await s.InitializeAsync();await s.BindAsync();var results=await Task.WhenAll(s.ApplyAsync(),s.ApplyAsync());
        Assert.Equal(new[]{"AlreadyApplied","Applied"},results.Select(x=>x.Result).Order(StringComparer.Ordinal));await s.AssertCountAsync(7);
    }
    [Fact]
    public async Task Sequence_gap_and_retired_receipt_never_reapply()
    {
        await using var s=new Scenario();await s.InitializeAsync();await s.BindAsync();
        using(var gap=await s.SendAsync("batches",s.Batch(2))){Assert.Equal(HttpStatusCode.Conflict,gap.StatusCode);Assert.Equal(1,(await gap.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("nextExpectedSequence").GetInt64());}
        await s.ApplyAsync();await using(var db=s.Api.Context())await db.Set<CredentialUsageBatch>().ExecuteDeleteAsync();
        using var retired=await s.SendAsync("batches",s.Batch());Assert.Equal(HttpStatusCode.Gone,retired.StatusCode);Assert.Equal(2,(await retired.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("nextExpectedSequence").GetInt64());await s.AssertCountAsync(7);Assert.Equal(2,(await s.BindAsync()).NextExpectedSequence);
    }
    [Fact]
    public async Task Naturally_expired_receipt_returns_retired_without_asserting_content_hash_or_counting()
    {
        await using var s=new Scenario{Observed=DateTimeOffset.UtcNow.AddDays(-2)};await s.InitializeAsync();await s.BindAsync();
        await using(var db=s.Api.Context())await db.Set<CredentialUsageCollector>().ExecuteUpdateAsync(x=>x.SetProperty(y=>y.AcceptedSequence,7L));
        using var response=await s.SendAsync("batches",s.Batch(sequence:7));Assert.Equal(HttpStatusCode.Gone,response.StatusCode);var body=await response.Content.ReadFromJsonAsync<JsonElement>();Assert.Equal(8,body.GetProperty("nextExpectedSequence").GetInt64());
        Assert.NotEqual("AlreadyApplied",body.TryGetProperty("result",out var result)?result.GetString():null);await using var after=s.Api.Context();Assert.Empty(await after.Set<CredentialUsageHourly>().ToArrayAsync());
    }
    [Theory]
    [InlineData("producer")][InlineData("sender")][InlineData("app")][InlineData("credential")][InlineData("api")][InlineData("release")][InlineData("version")][InlineData("sequence")][InlineData("hash")][InlineData("grant")][InlineData("credentialWindow")][InlineData("environment")]
    public async Task Unproven_snapshot_grant_or_instance_is_rejected(string fault)
    {
        await using var s=new Scenario();await s.InitializeAsync();await s.BindAsync();var batch=s.Batch();var item=batch.Core.Items[0];
        batch=fault switch{
            "producer"=>batch with{Core=batch.Core with{ProducerInstanceId=Guid.NewGuid()}},
            "sender"=>batch with{SenderInstanceId=Guid.NewGuid()},
            "app"=>batch with{Core=batch.Core with{Items=[item with{ApplicationId=Guid.NewGuid()}]}},
            "credential"=>batch with{Core=batch.Core with{Items=[item with{CredentialId=Guid.NewGuid()}]}},
            "api"=>batch with{Core=batch.Core with{Items=[item with{ApiId=Guid.NewGuid()}]}},
            "release"=>batch with{Core=batch.Core with{Items=[item with{Source=s.Source with{ReleaseId=Guid.NewGuid()}}]}},
            "version"=>batch with{Core=batch.Core with{Items=[item with{Source=s.Source with{ConfigVersion=2}}]}},
            "sequence"=>batch with{Core=batch.Core with{Items=[item with{Source=s.Source with{DeploymentSequence=2}}]}},
            "hash"=>batch with{Core=batch.Core with{Items=[item with{Source=s.Source with{PayloadHash=new string('b',64)}}]}},
            "grant"=>batch with{Core=batch.Core with{Items=[item with{GrantValidFrom=item.GrantValidFrom.AddMinutes(-1)}]}},
            _=>batch};
        if(fault=="credentialWindow")
        {
            // Change immutable fixture credential validity and recompute the stored source, so hash verification still succeeds.
            var snapshot=JsonSerializer.Deserialize<RuntimeSnapshot>(s.Payload,CanonicalJson.Options)!;var app=snapshot.Applications[0];var credential=app.Credentials[0] with{ValidFrom=s.Observed.AddSeconds(1)};
            var bytes=CanonicalJson.Serialize(snapshot with{Applications=[app with{Credentials=[credential]}]});var hash=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
            await using var db=s.Api.Context();await db.Set<GatewayConfigSnapshot>().ExecuteUpdateAsync(x=>x.SetProperty(y=>y.PayloadBytes,bytes).SetProperty(y=>y.SizeBytes,bytes.Length));await db.Set<GatewayConfigVersion>().ExecuteUpdateAsync(x=>x.SetProperty(y=>y.SnapshotHash,hash));
            batch=batch with{Core=batch.Core with{Items=[item with{Source=s.Source with{PayloadHash=hash}}]}};
        }
        if(fault=="environment"){await using var db=s.Api.Context();var v=await db.Set<GatewayConfigVersion>().SingleAsync();var other=new EnvironmentRecord{ProjectId=s.Api.Project.Id,Code="OTHER",Name="Other"};db.Add(other);await db.SaveChangesAsync();v.EnvironmentId=other.Id;await db.SaveChangesAsync();}
        using var response=await s.SendAsync("batches",batch);Assert.Contains(response.StatusCode,new[]{HttpStatusCode.Conflict,HttpStatusCode.UnprocessableEntity});
        await using var after=s.Api.Context();Assert.Empty(await after.Set<CredentialUsageHourly>().ToArrayAsync());Assert.Empty(await after.Set<CredentialUsageBatch>().ToArrayAsync());
    }
    [Fact]
    public async Task New_sender_replays_proven_old_producer_without_changing_core()
    {
        await using var s=new Scenario();await s.InitializeAsync();await s.BindAsync();var old=s.Batch();s.Instance=Guid.NewGuid();
        await using(var db=s.Api.Context()){var node=await db.Set<GatewayNode>().SingleAsync();node.InstanceId=s.Instance.ToString();db.Add(new GatewayNodeEvent{GatewayNodeId=node.Id,EventType="registered",Detail=JsonSerializer.Serialize(new{instanceId=s.Instance})});await db.SaveChangesAsync();}
        await s.BindAsync();Assert.Equal("Applied",(await s.ApplyAsync(old with{SenderInstanceId=s.Instance})).Result);Assert.Equal("AlreadyApplied",(await s.ApplyAsync(old with{SenderInstanceId=s.Instance})).Result);await s.AssertCountAsync(7);
    }
    [Fact]
    public async Task Registered_event_proves_offline_old_producer_but_not_other_node()
    {
        await using var s=new Scenario();await s.InitializeAsync();var old=s.Batch();s.Instance=Guid.NewGuid();
        await using(var db=s.Api.Context()){var node=await db.Set<GatewayNode>().SingleAsync();node.InstanceId=s.Instance.ToString();await db.SaveChangesAsync();}
        await s.BindAsync();Assert.Equal("Applied",(await s.ApplyAsync(old with{SenderInstanceId=s.Instance})).Result);
        await using var after=s.Api.Context();Assert.Equal("registered",(await after.Set<CredentialUsageCollectorInstance>().SingleAsync(x=>x.InstanceId==old.Core.ProducerInstanceId)).IdentityProof);
    }
    [Fact]
    public async Task Old_working_revocation_does_not_discard_published_use()
    {
        await using var s=new Scenario();await s.InitializeAsync();await s.BindAsync();
        await using(var db=s.Api.Context()){await db.Set<ApplicationCredential>().ExecuteUpdateAsync(x=>x.SetProperty(y=>y.Status,"Revoked"));await db.Set<ApplicationRecord>().ExecuteUpdateAsync(x=>x.SetProperty(y=>y.Status,"Disabled"));}
        await s.ApplyAsync();await s.AssertCountAsync(7);
    }
    [Fact]
    public async Task Two_collectors_have_independent_sequences_and_monotonic_last_used()
    {
        await using var s=new Scenario();await s.InitializeAsync();await s.BindAsync();await s.ApplyAsync();var other=Guid.NewGuid();
        using(var bind=await s.SendAsync("collectors",new UsageCollectorRequest("1.0",s.Instance,other)))Assert.Equal(HttpStatusCode.OK,bind.StatusCode);
        var first=s.Batch();await s.ApplyAsync(first with{Core=first.Core with{CollectorId=other,Items=[first.Core.Items[0] with{FirstObservedAt=s.Observed.AddSeconds(-1),LastObservedAt=s.Observed.AddSeconds(-1)}]}});
        await s.AssertCountAsync(14);await using var db=s.Api.Context();Assert.Equal(s.Observed,(await db.Set<ApplicationCredential>().SingleAsync()).LastUsedAt);Assert.Equal(2,await db.Set<CredentialUsageCollector>().CountAsync());
    }
    [Fact]
    public async Task Same_sequence_on_different_nodes_adds_each_observation_once()
    {
        await using var s=new Scenario();await s.InitializeAsync();await s.BindAsync();await s.ApplyAsync();var second=new GatewayNode{EnvironmentId=s.Api.Environment.Id,NodeName="usage-second",InstanceId=s.Instance.ToString(),IdentityHash=s.Node.IdentityHash,Enabled=true};
        await using(var db=s.Api.Context()){db.Add(second);await db.SaveChangesAsync();}
        using(var bound=await s.SendAsync("collectors",new UsageCollectorRequest("1.0",s.Instance,s.Collector),second.Id))Assert.Equal(HttpStatusCode.OK,bound.StatusCode);
        using(var applied=await s.SendAsync("batches",s.Batch(),second.Id))Assert.Equal(HttpStatusCode.OK,applied.StatusCode);
        using(var replay=await s.SendAsync("batches",s.Batch(),second.Id)){Assert.Equal(HttpStatusCode.OK,replay.StatusCode);Assert.Equal("AlreadyApplied",(await replay.Content.ReadFromJsonAsync<UsageReceipt>())!.Result);}
        await s.AssertCountAsync(14);
    }
    [Fact]
    public async Task Disable_that_wins_row_lock_blocks_inflight_ingest()
    {
        await using var s=new Scenario();await s.InitializeAsync();await s.BindAsync();await using var db=s.Api.Context();await using var transaction=await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM gateway_nodes WHERE id={s.Node.Id} FOR UPDATE");
        s.Sql.NodeLock=new(TaskCreationOptions.RunContinuationsAsynchronously);var request=s.SendAsync("batches",s.Batch());
        await s.Sql.NodeLock.Task.WaitAsync(TimeSpan.FromSeconds(8));await db.Set<GatewayNode>().Where(x=>x.Id==s.Node.Id).ExecuteUpdateAsync(x=>x.SetProperty(y=>y.Enabled,false));await transaction.CommitAsync();
        using var response=await request;Assert.Equal(HttpStatusCode.Unauthorized,response.StatusCode);Assert.Empty(await db.Set<CredentialUsageHourly>().ToArrayAsync());
    }
    [Fact]
    public async Task Hundred_items_use_bounded_snapshot_reads_and_keep_windows_separate()
    {
        await using var s=new Scenario();await s.InitializeAsync();await s.BindAsync();var snapshot=JsonSerializer.Deserialize<RuntimeSnapshot>(s.Payload,CanonicalJson.Options)!;var app=snapshot.Applications[0];
        var grants=Enumerable.Range(1,100).Select(i=>new RuntimePermission(s.Api.Api.Id,s.Observed.AddMinutes(-i),s.Observed.AddMinutes(i))).ToArray();
        var bytes=CanonicalJson.Serialize(snapshot with{Applications=[app with{Permissions=grants}]});var hash=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        await using(var db=s.Api.Context()){await db.Set<GatewayConfigSnapshot>().ExecuteUpdateAsync(x=>x.SetProperty(y=>y.PayloadBytes,bytes).SetProperty(y=>y.SizeBytes,bytes.Length));await db.Set<GatewayConfigVersion>().ExecuteUpdateAsync(x=>x.SetProperty(y=>y.SnapshotHash,hash));}
        var batch=s.Batch();var item=batch.Core.Items[0];batch=batch with{Core=batch.Core with{Items=grants.Select(g=>item with{GrantValidFrom=g.ValidFrom,GrantExpiresAt=g.ExpiresAt,Source=s.Source with{PayloadHash=hash}}).ToArray()}};
        s.Sql.SnapshotQueries=0;await s.ApplyAsync(batch);Assert.InRange(s.Sql.SnapshotQueries,1,2);await s.AssertCountAsync(700);
    }
    [Fact]
    public async Task Empty_pulse_retains_explicit_gap_and_durable_watermark()
    {
        await using var s=new Scenario();await s.InitializeAsync();await s.BindAsync();var batch=s.Batch();batch=batch with{Core=batch.Core with{Items=[],Gaps=[new(s.Observed.AddSeconds(-1),s.Observed,"CrashTail")]}};
        var receipt=await s.ApplyAsync(batch);Assert.Equal(batch.Core.DurableThrough,receipt.AcceptedThrough);
        await using var db=s.Api.Context();Assert.Empty(await db.Set<CredentialUsageHourly>().ToArrayAsync());var coverage=await db.Set<CredentialUsageCoverage>().SingleAsync(x=>x.HourStart==s.Hour);
        Assert.Equal("Partial",coverage.State);Assert.Contains("CrashTail",coverage.Gaps,StringComparison.Ordinal);Assert.Contains("from",coverage.Intervals,StringComparison.Ordinal);Assert.Equal(batch.Core.DurableThrough,coverage.AcceptedThrough);
    }
    [Fact]
    public async Task Deployed_pending_age_limit_rejects_older_new_sequence()
    {
        await using var s=new Scenario{Observed=DateTimeOffset.UtcNow.AddHours(-2)};await s.InitializeAsync(pendingAgeHours:1);await s.BindAsync();
        using var response=await s.SendAsync("batches",s.Batch());Assert.Equal(HttpStatusCode.UnprocessableEntity,response.StatusCode);await using var db=s.Api.Context();Assert.Empty(await db.Set<CredentialUsageHourly>().ToArrayAsync());
    }
    [Fact]
    public async Task Future_clock_is_rejected_and_leaves_controlled_source_fault()
    {
        await using var s=new Scenario();await s.InitializeAsync();await s.BindAsync();var batch=s.Batch();var future=DateTimeOffset.UtcNow.AddMinutes(3);var hour=new DateTimeOffset(future.Year,future.Month,future.Day,future.Hour,0,0,TimeSpan.Zero);
        batch=batch with{Core=batch.Core with{SealedAt=future,DurableThrough=future,Intervals=[],Items=[batch.Core.Items[0] with{HourStart=hour,FirstObservedAt=future,LastObservedAt=future}]}};
        using var response=await s.SendAsync("batches",batch);Assert.Equal(HttpStatusCode.UnprocessableEntity,response.StatusCode);await using var db=s.Api.Context();Assert.Empty(await db.Set<CredentialUsageHourly>().ToArrayAsync());
        Assert.Contains(await db.Set<CredentialUsageCoverage>().ToArrayAsync(),x=>x.State=="Partial"&&x.Gaps.Contains("ClockSkew",StringComparison.Ordinal));
    }
    [Fact]
    public async Task Configured_processing_deadline_includes_reading_chunked_body()
    {
        await using var s=new Scenario();await s.InitializeAsync(ingestTimeoutSeconds:1);await s.BindAsync();
        using var request=new HttpRequestMessage(HttpMethod.Post,$"/internal/v1/nodes/{s.Node.Id}/credential-usage/batches"){Content=new SlowEofContent(JsonSerializer.SerializeToUtf8Bytes(s.Batch(),UsageProtocolJson.Options))};request.Content.Headers.ContentType=new("application/json");request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",s.Secret);
        using var response=await s.Api.Client.SendAsync(request);Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);await using var db=s.Api.Context();Assert.Empty(await db.Set<CredentialUsageHourly>().ToArrayAsync());
    }
    [Fact]
    public async Task Full_receive_slots_reject_before_body_validation()
    {
        await using var s=new Scenario();await s.InitializeAsync();await s.BindAsync();using var services=s.Api.Services();var slots=services.ServiceProvider.GetRequiredService<UsageIngestBudget>();using var first=slots.Enter();using var second=slots.Enter();
        using var request=new HttpRequestMessage(HttpMethod.Post,$"/internal/v1/nodes/{s.Node.Id}/credential-usage/batches"){Content=new StringContent(new string(' ',256*1024)+"{}",Encoding.UTF8,"application/json")};request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",s.Secret);
        using var response=await s.Api.Client.SendAsync(request);Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);Assert.Equal("usage_ingest_busy",(await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }
    [Fact]
    public async Task Overflow_rejects_whole_batch_and_records_controlled_gap()
    {
        await using var s=new Scenario();await s.InitializeAsync();await s.BindAsync();
        await using(var db=s.Api.Context()){db.Add(new CredentialUsageHourly{EnvironmentId=s.Api.Environment.Id,CredentialId=s.Credential.Id,ApplicationId=s.Api.Application.Id,HourStart=s.Hour,Count=long.MaxValue-3,LastUsedAt=s.Observed.AddSeconds(-1)});await db.SaveChangesAsync();}
        using var response=await s.SendAsync("batches",s.Batch());Assert.Equal(HttpStatusCode.Conflict,response.StatusCode);await s.AssertCountAsync(long.MaxValue-3);
        await using var after=s.Api.Context();Assert.Empty(await after.Set<CredentialUsageBatch>().ToArrayAsync());Assert.Equal(0,(await after.Set<CredentialUsageCollector>().SingleAsync()).AcceptedSequence);Assert.Null((await after.Set<ApplicationCredential>().SingleAsync()).LastUsedAt);
        var coverage=await after.Set<CredentialUsageCoverage>().SingleAsync();Assert.Equal("Partial",coverage.State);Assert.Contains("CountOverflow",coverage.Gaps,StringComparison.Ordinal);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Disabled_or_wrong_secret_node_cannot_bind_or_upload(bool disable)
    {
        await using var s=new Scenario();await s.InitializeAsync();await s.BindAsync();if(disable){await using var db=s.Api.Context();await db.Set<GatewayNode>().ExecuteUpdateAsync(x=>x.SetProperty(y=>y.Enabled,false));}
        foreach(var suffix in new[]{"collectors","batches"})
        {
            object body=suffix=="collectors"?new UsageCollectorRequest("1.0",s.Instance,s.Collector):s.Batch();using var response=await s.SendAsync(suffix,body,secret:disable?null:new string('x',48));Assert.Equal(HttpStatusCode.Unauthorized,response.StatusCode);
        }
    }
    [Theory]
    [InlineData("duplicate")][InlineData("unknown")][InlineData("size")]
    public async Task Internal_batch_http_rejects_ambiguous_or_oversized_json(string fault)
    {
        await using var s=new Scenario();await s.InitializeAsync();await s.BindAsync();var json=JsonSerializer.Serialize(s.Batch(),UsageProtocolJson.Options);
        json=fault switch{"duplicate"=>"{\"senderInstanceId\":\""+s.Instance+"\","+json[1..],"unknown"=>"{\"organizationId\":\""+Guid.NewGuid()+"\","+json[1..],_=>new string(' ',256*1024)+json};
        using var request=new HttpRequestMessage(HttpMethod.Post,$"/internal/v1/nodes/{s.Node.Id}/credential-usage/batches"){Content=new StringContent(json,Encoding.UTF8,"application/json")};request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",s.Secret);
        using var response=await s.Api.Client.SendAsync(request);Assert.Equal(fault=="size"?HttpStatusCode.RequestEntityTooLarge:HttpStatusCode.UnprocessableEntity,response.StatusCode);
    }
}
