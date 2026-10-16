using WebApi.Gateway.CredentialUsage;
using Xunit;
namespace WebApi.Gateway.Tests;

public sealed class UsageJournalOrphanTests
{
    private sealed class Fixture:IDisposable
    {
        public string Root {get;}=Path.Combine(Path.GetTempPath(),"usage-orphan-"+Guid.NewGuid().ToString("N"));
        public GatewaySettings Settings {get;}
        public string DirectoryPath=>Path.Combine(Settings.LkgDirectory,"credential-usage");
        public string CheckpointPath=>Path.Combine(DirectoryPath,"checkpoint.json");
        public Fixture(){Directory.CreateDirectory(Root);if(!OperatingSystem.IsWindows())File.SetUnixFileMode(Root,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);var secret=Path.Combine(Root,"node.secret");File.WriteAllText(secret,new string('s',48));if(!OperatingSystem.IsWindows())File.SetUnixFileMode(secret,UnixFileMode.UserRead|UnixFileMode.UserWrite);Settings=new(Guid.NewGuid(),"orphan-node",secret,Path.Combine(Root,"lkg"),new("http://control-plane:8080"),false,Guid.NewGuid());}
        public UsageCheckpoint State()=>UsageRecoveryTests.Checkpoint(UsageRecoveryTests.Observation() with{EnvironmentId=Settings.EnvironmentId}) with{NodeName=Settings.NodeName};
        public async Task<byte[]> PrepareAsync(){using(var journal=new UsageJournal(Settings,UsageRecoveryTests.Options()))await journal.SaveAsync(State());return await File.ReadAllBytesAsync(CheckpointPath);}
        public async Task<string> OrphanAsync(byte[] bytes){var file=Path.Combine(DirectoryPath,".checkpoint-"+Guid.NewGuid().ToString("N")+".tmp");await File.WriteAllBytesAsync(file,bytes);if(!OperatingSystem.IsWindows())File.SetUnixFileMode(file,UnixFileMode.UserRead|UnixFileMode.UserWrite);return file;}
        public void Dispose(){if(Directory.Exists(Root))Directory.Delete(Root,true);}
    }
    [Fact]
    public async Task Interrupted_write_is_retained_without_adopting_uncertain_delta()
    {
        using var f=new Fixture();var committed=await f.PrepareAsync();byte[] interrupted;
        using(var alternate=new UsageJournal(f.Settings with{LkgDirectory=Path.Combine(f.Root,"other")},UsageRecoveryTests.Options())){var c=f.State();await alternate.SaveAsync(c with{Active=[c.Active[0] with{Item=c.Active[0].Item with{Count=99}}]});interrupted=await File.ReadAllBytesAsync(alternate.FilePath);}
        var orphan=await f.OrphanAsync(interrupted);using var next=new UsageJournal(f.Settings with{InstanceId=Guid.NewGuid()},UsageRecoveryTests.Options());var state=await next.LoadAsync();Assert.NotNull(state);Assert.Equal(1,Assert.Single(state.Active).Item.Count);Assert.Contains(state.Gaps,g=>g.Reason=="CrashTail");Assert.False(File.Exists(orphan));Assert.Equal(committed,await File.ReadAllBytesAsync(f.CheckpointPath));var retained=Assert.Single(Directory.GetFiles(f.DirectoryPath,"checkpoint.quarantine-*"));Assert.Equal(interrupted,await File.ReadAllBytesAsync(retained));
    }
    [Fact]
    public async Task Readable_foreign_temporary_checkpoint_is_not_moved_or_adopted()
    {
        using var f=new Fixture();var committed=await f.PrepareAsync();using var other=new Fixture();var foreign=await other.PrepareAsync();var orphan=await f.OrphanAsync(foreign);using var next=new UsageJournal(f.Settings with{InstanceId=Guid.NewGuid()},UsageRecoveryTests.Options());await Assert.ThrowsAsync<InvalidDataException>(()=>next.LoadAsync());Assert.Equal(committed,await File.ReadAllBytesAsync(f.CheckpointPath));Assert.Equal(foreign,await File.ReadAllBytesAsync(orphan));Assert.Empty(Directory.GetFiles(f.DirectoryPath,"checkpoint.quarantine-*"));
    }
    [Fact]
    public async Task Excess_orphan_material_blocks_new_checkpoint_growth_before_any_write()
    {
        using var f=new Fixture();var committed=await f.PrepareAsync();for(var i=0;i<3;i++)await f.OrphanAsync(committed);using var next=new UsageJournal(f.Settings with{InstanceId=Guid.NewGuid()},UsageRecoveryTests.Options());await Assert.ThrowsAsync<IOException>(()=>next.LoadAsync());await Assert.ThrowsAsync<IOException>(()=>next.SaveAsync(f.State() with{Active=[]}));Assert.Equal(committed,await File.ReadAllBytesAsync(f.CheckpointPath));Assert.Equal(3,Directory.GetFiles(f.DirectoryPath,".checkpoint-*.tmp").Length);Assert.False(File.Exists(Path.Combine(f.DirectoryPath,"failure-summary.json")));
    }
    [Theory][InlineData(".checkpoint-")][InlineData("clean-stop.json-")][InlineData("failure-summary.json-")]
    public async Task A_partial_first_write_is_retained_and_signalled_when_no_committed_checkpoint_exists(string prefix)
    {
        using var f=new Fixture();await f.PrepareAsync();File.Delete(f.CheckpointPath);var orphan=await f.OrphanAsync("{\"partial\":"u8.ToArray());if(prefix!=".checkpoint-")File.Move(orphan,Path.Combine(f.DirectoryPath,prefix+Guid.NewGuid().ToString("N")+".tmp"));using var next=new UsageJournal(f.Settings with{InstanceId=Guid.NewGuid()},UsageRecoveryTests.Options());Assert.Null(await next.LoadAsync());var property=typeof(UsageJournal).GetProperty("RecoveredOrphanedWrite");Assert.NotNull(property);Assert.Equal(true,property.GetValue(next));Assert.Single(Directory.GetFiles(f.DirectoryPath,"checkpoint.quarantine-*"));Assert.Empty(Directory.GetFiles(f.DirectoryPath,".checkpoint-*.tmp"));
    }
}
