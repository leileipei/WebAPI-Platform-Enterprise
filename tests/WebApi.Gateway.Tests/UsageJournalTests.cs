using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.CredentialUsage;
using WebApi.Domain.CredentialUsage;
using WebApi.Gateway;
using WebApi.Gateway.CredentialUsage;
using Xunit;
namespace WebApi.Gateway.Tests;

public sealed class UsageJournalTests
{
    private sealed class Fixture:IDisposable
    {
        public string Root {get;}=Path.Combine(Path.GetTempPath(),"usage-journal-"+Guid.NewGuid().ToString("N"));
        public GatewaySettings Settings {get;}
        public Fixture(){Directory.CreateDirectory(Root);if(!OperatingSystem.IsWindows())File.SetUnixFileMode(Root,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);var secret=Path.Combine(Root,"node.secret");File.WriteAllText(secret,new string('s',48));if(!OperatingSystem.IsWindows())File.SetUnixFileMode(secret,UnixFileMode.UserRead|UnixFileMode.UserWrite);Settings=new(Guid.NewGuid(),"test-node",secret,Path.Combine(Root,"lkg"),new("http://127.0.0.1:1/"),false,Guid.NewGuid());}
        public UsageCheckpoint Checkpoint(){var a=UsageRecoveryTests.Observation() with{EnvironmentId=Settings.EnvironmentId};return UsageRecoveryTests.Checkpoint(a);}
        public string FilePath=>Path.Combine(Settings.LkgDirectory,"credential-usage","checkpoint.json");
        public void Dispose(){if(!Directory.Exists(Root))return;if(!OperatingSystem.IsWindows())foreach(var dir in Directory.EnumerateDirectories(Root,"*",SearchOption.AllDirectories))if(new DirectoryInfo(dir).LinkTarget is null)File.SetUnixFileMode(dir,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);Directory.Delete(Root,true);}
    }
    private sealed class JournalHandle:IDisposable
    {
        public object Journal {get;}
        public Func<UsageCheckpoint,CancellationToken,Task> Save {get;}
        public Func<CancellationToken,Task<UsageCheckpoint?>> Load {get;}
        public JournalHandle(GatewaySettings settings,IUsageJournalFileSystem? files=null)
        {
            var type=typeof(UsageCheckpoint).Assembly.GetType("WebApi.Gateway.CredentialUsage.UsageJournal");Assert.NotNull(type);
            var services=new ServiceCollection().AddSingleton(settings).AddSingleton(UsageRecoveryTests.Options()).AddSingleton<TimeProvider>(TimeProvider.System);if(files is not null)services.AddSingleton(files);
            using var provider=services.BuildServiceProvider();Journal=ActivatorUtilities.CreateInstance(provider,type);
            Save=type.GetMethod("SaveAsync")!.CreateDelegate<Func<UsageCheckpoint,CancellationToken,Task>>(Journal);Load=type.GetMethod("LoadAsync")!.CreateDelegate<Func<CancellationToken,Task<UsageCheckpoint?>>>(Journal);
        }
        public void Dispose()=>((IDisposable)Journal).Dispose();
    }
    private sealed class FaultingFiles(IUsageJournalFileSystem inner,string point):IUsageJournalFileSystem
    {
        private bool replaced;
        public Task<byte[]> ReadAsync(string path,CancellationToken ct)=>inner.ReadAsync(path,ct);
        public async Task WriteDurableAsync(string path,ReadOnlyMemory<byte> bytes,CancellationToken ct){if(point=="before-write")throw new IOException("synthetic write failure");await inner.WriteDurableAsync(path,bytes,ct);if(point=="after-file-fsync")throw new IOException("synthetic file durability boundary");}
        public void Replace(string source,string destination){if(point=="before-rename")throw new IOException("synthetic rename failure");inner.Replace(source,destination);replaced=true;}
        public void SyncDirectory(string path){if(point=="after-rename"&&replaced)throw new IOException("synthetic directory durability failure");inner.SyncDirectory(path);}
    }
    private static IUsageJournalFileSystem Physical()
    {var type=typeof(UsageCheckpoint).Assembly.GetType("WebApi.Gateway.CredentialUsage.PhysicalUsageJournalFileSystem");Assert.NotNull(type);return Assert.IsAssignableFrom<IUsageJournalFileSystem>(Activator.CreateInstance(type));}
    [Fact]
    public async Task Atomic_checkpoint_roundtrip_preserves_active_pending_and_original_core_hash()
    {
        using var f=new Fixture();var c=f.Checkpoint();var core=new UsageBatchCore("1.0",f.Settings.InstanceId,c.CollectorId,1,UsageRecoveryTests.Now,UsageRecoveryTests.Now,[],[],[c.Active[0].Item]);c=c with{Active=[],Pending=[core],NextSequence=2};
        using(var journal=new JournalHandle(f.Settings)){Assert.Null(await journal.Load(default));await journal.Save(c,default);}
        using(var restored=new JournalHandle(f.Settings)){var loaded=await restored.Load(default);Assert.NotNull(loaded);Assert.Equal(JsonSerializer.Serialize(c,UsageProtocolJson.Options),JsonSerializer.Serialize(loaded,UsageProtocolJson.Options));Assert.Equal(UsageBatchRules.CoreHash(core),UsageBatchRules.CoreHash(loaded.Pending[0]));}
        if(!OperatingSystem.IsWindows()){Assert.Equal(UnixFileMode.UserRead|UnixFileMode.UserWrite,File.GetUnixFileMode(f.FilePath));Assert.Equal(UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute,File.GetUnixFileMode(Path.GetDirectoryName(f.FilePath)!));}
    }
    [Theory][InlineData("before-write")][InlineData("after-file-fsync")][InlineData("before-rename")][InlineData("after-rename")]
    public async Task Failure_at_durability_boundary_leaves_one_whole_valid_checkpoint(string point)
    {
        using var f=new Fixture();var before=f.Checkpoint();using(var first=new JournalHandle(f.Settings))await first.Save(before,default);
        var after=before with{Active=[before.Active[0] with{Item=before.Active[0].Item with{Count=9}}]};
        using(var failing=new JournalHandle(f.Settings,new FaultingFiles(Physical(),point)))await Assert.ThrowsAsync<IOException>(()=>failing.Save(after,default));
        using var recovery=new JournalHandle(f.Settings);var loaded=await recovery.Load(default);Assert.NotNull(loaded);var count=Assert.Single(loaded.Active).Item.Count;Assert.Equal(point=="after-rename"?9:1,count);
    }
    [Fact]
    public async Task Changed_instance_adds_volatile_tail_gap_without_changing_frozen_pending_core()
    {
        using var f=new Fixture();var c=f.Checkpoint();using(var first=new JournalHandle(f.Settings))await first.Save(c,default);
        using var restarted=new JournalHandle(f.Settings with{InstanceId=Guid.NewGuid()});var loaded=await restarted.Load(default);Assert.NotNull(loaded);Assert.Contains(loaded.Gaps,x=>x.Reason=="CrashTail"&&x.From<=c.DurableThrough&&x.To>=x.From);Assert.Equal(c.Active[0].Item.Count,loaded.Active[0].Item.Count);
    }
    [Fact]
    public async Task New_writer_must_seal_old_active_delta_before_saving_new_instance_identity()
    {
        using var f=new Fixture();using(var first=new JournalHandle(f.Settings))await first.Save(f.Checkpoint(),default);var before=await File.ReadAllBytesAsync(f.FilePath);
        using var restart=new JournalHandle(f.Settings with{InstanceId=Guid.NewGuid()});var restored=await restart.Load(default);Assert.NotNull(restored);await Assert.ThrowsAsync<InvalidDataException>(()=>restart.Save(restored,default));Assert.Equal(before,await File.ReadAllBytesAsync(f.FilePath));
    }
    [Fact]
    public async Task Direct_save_cannot_bypass_configured_pending_byte_limit()
    {
        using var f=new Fixture();using var journal=new JournalHandle(f.Settings);var c=f.Checkpoint();var items=Enumerable.Range(0,100).Select(_=>c.Active[0].Item with{CredentialId=Guid.NewGuid()}).ToArray();
        var pending=Enumerable.Range(1,200).Select(i=>new UsageBatchCore("1.0",f.Settings.InstanceId,c.CollectorId,i,UsageRecoveryTests.Now,UsageRecoveryTests.Now,[],[],items)).ToArray();c=c with{Active=[],Pending=pending,NextSequence=201};Assert.True(JsonSerializer.SerializeToUtf8Bytes(pending,UsageProtocolJson.Options).Length>8*1024*1024);
        await Assert.ThrowsAsync<InvalidDataException>(()=>journal.Save(c,default));Assert.False(File.Exists(f.FilePath));
    }
    [Fact]
    public async Task Corrupt_file_is_retained_in_quarantine_and_never_overwritten_in_place()
    {
        using var f=new Fixture();using(var first=new JournalHandle(f.Settings))await first.Save(f.Checkpoint(),default);var corrupt="{\"truncated\":";await File.WriteAllTextAsync(f.FilePath,corrupt);
        using var recovery=new JournalHandle(f.Settings);await Assert.ThrowsAsync<InvalidDataException>(()=>recovery.Load(default));var files=Directory.EnumerateFiles(f.Settings.LkgDirectory,"*",SearchOption.AllDirectories).Where(p=>Path.GetFileName(p).Contains("quarantine",StringComparison.Ordinal)).ToArray();Assert.NotEmpty(files);Assert.Contains(files,p=>File.ReadAllText(p)==corrupt);
    }
    [Theory][InlineData("node")][InlineData("environment")][InlineData("origin")]
    public async Task Wrong_identity_or_control_plane_clone_cannot_replay_saved_queue(string mismatch)
    {
        using var f=new Fixture();using(var first=new JournalHandle(f.Settings))await first.Save(f.Checkpoint(),default);
        var before=await File.ReadAllBytesAsync(f.FilePath);
        var settings=mismatch switch{"node"=>f.Settings with{NodeName="other-node"},"environment"=>f.Settings with{EnvironmentId=Guid.NewGuid()},_=>f.Settings with{ControlPlaneUrl=new("http://127.0.0.1:2/")}};
        using var foreign=new JournalHandle(settings);await Assert.ThrowsAsync<InvalidDataException>(()=>foreign.Load(default));Assert.Equal(before,await File.ReadAllBytesAsync(f.FilePath));
    }
    [Fact]
    public async Task Symbolic_link_path_and_second_writer_are_rejected()
    {
        using var f=new Fixture();Directory.CreateDirectory(f.Settings.LkgDirectory);var outside=Path.Combine(f.Root,"outside");Directory.CreateDirectory(outside);Directory.CreateSymbolicLink(Path.Combine(f.Settings.LkgDirectory,"credential-usage"),outside);
        using(var linked=new JournalHandle(f.Settings))await Assert.ThrowsAnyAsync<IOException>(()=>linked.Save(f.Checkpoint(),default));
        Directory.Delete(Path.Combine(f.Settings.LkgDirectory,"credential-usage"));using var first=new JournalHandle(f.Settings);await first.Save(f.Checkpoint(),default);using var second=new JournalHandle(f.Settings);await Assert.ThrowsAnyAsync<IOException>(()=>second.Load(default));
    }
    [Fact]
    public async Task Readonly_directory_and_cancelled_save_keep_prior_checkpoint_bytes()
    {
        using var f=new Fixture();using var journal=new JournalHandle(f.Settings);var checkpoint=f.Checkpoint();await journal.Save(checkpoint,default);var before=await File.ReadAllBytesAsync(f.FilePath);
        using(var cancelled=new CancellationTokenSource()){cancelled.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>journal.Save(checkpoint,cancelled.Token));}Assert.Equal(before,await File.ReadAllBytesAsync(f.FilePath));
        if(!OperatingSystem.IsWindows()){File.SetUnixFileMode(Path.GetDirectoryName(f.FilePath)!,UnixFileMode.UserRead|UnixFileMode.UserExecute);await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(()=>journal.Save(checkpoint,default));Assert.Equal(before,await File.ReadAllBytesAsync(f.FilePath));}
    }
    [Fact]
    public async Task Clean_stop_is_bound_to_exact_checkpoint_and_is_distinct_from_a_crash()
    {
        using var f=new Fixture();var c=f.Checkpoint() with{Active=[]};
        using(var first=new JournalHandle(f.Settings))
        {
            await first.Save(c,default);var method=first.Journal.GetType().GetMethod("MarkCleanStopAsync");Assert.NotNull(method);
            await (Task)method.Invoke(first.Journal,[CancellationToken.None])!;
        }
        using var restart=new JournalHandle(f.Settings with{InstanceId=Guid.NewGuid()});var loaded=await restart.Load(default);Assert.NotNull(loaded);
        Assert.DoesNotContain(loaded.Gaps,x=>x.Reason=="CrashTail");Assert.Contains(loaded.Gaps,x=>x.Reason=="NodeStopped");
    }
    [Fact]
    public async Task Epoch_retirement_keeps_bounded_private_failure_summary_before_replacement()
    {
        using var f=new Fixture();var c=f.Checkpoint() with{Active=[]};using var journal=new JournalHandle(f.Settings);await journal.Save(c,default);
        var replacement=c with{CollectorId=Guid.NewGuid(),Gaps=[new(c.DurableThrough,c.DurableThrough.AddTicks(1),"PendingCapacity")]};await journal.Save(replacement,default);
        var path=Path.Combine(Path.GetDirectoryName(f.FilePath)!,"failure-summary.json");Assert.True(File.Exists(path));Assert.InRange(new FileInfo(path).Length,1,16384);
        using var summary=JsonDocument.Parse(await File.ReadAllBytesAsync(path));Assert.Equal(c.CollectorId,summary.RootElement.GetProperty("previousCollectorId").GetGuid());Assert.Equal(replacement.CollectorId,summary.RootElement.GetProperty("collectorId").GetGuid());
        if(!OperatingSystem.IsWindows())Assert.Equal(UnixFileMode.UserRead|UnixFileMode.UserWrite,File.GetUnixFileMode(path));
    }

}
