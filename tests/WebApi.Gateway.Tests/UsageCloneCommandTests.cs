using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using WebApi.Contracts.CredentialUsage;
using WebApi.Domain.CredentialUsage;
using WebApi.Gateway.CredentialUsage;
using Xunit;
namespace WebApi.Gateway.Tests;

public sealed class UsageCloneCommandTests
{
    private sealed class Fixture:IDisposable
    {
        public string Root {get;}=Path.Combine(Path.GetTempPath(),"usage-clone-"+Guid.NewGuid().ToString("N"));
        public GatewaySettings Settings {get;}
        public Guid SourceOwner {get;}=Guid.NewGuid();
        public Guid TargetOwner {get;}=Guid.NewGuid();
        public string Checkpoint=>Path.Combine(Settings.LkgDirectory,"credential-usage","checkpoint.json");
        public byte[] Original {get;private set;}=[];
        public UsageCheckpoint Before {get;private set;}=null!;
        public Fixture(){Directory.CreateDirectory(Root);if(!OperatingSystem.IsWindows())File.SetUnixFileMode(Root,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);var secret=Path.Combine(Root,"node.secret");File.WriteAllText(secret,new string('s',48));if(!OperatingSystem.IsWindows())File.SetUnixFileMode(secret,UnixFileMode.UserRead|UnixFileMode.UserWrite);Settings=new(Guid.NewGuid(),"clone-node",secret,Path.Combine(Root,"lkg"),new("http://control-plane:8080/"),false,Guid.NewGuid());}
        public async Task PrepareAsync(){var now=DateTimeOffset.UtcNow;var hour=new DateTimeOffset(now.Year,now.Month,now.Day,now.Hour,0,0,TimeSpan.Zero);var item=new UsageItem(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),hour.AddHours(-1),null,new(Guid.NewGuid(),1,1,new string('a',64)),hour,1,now,now);var collector=Guid.NewGuid();var core=new UsageBatchCore("1.0",Settings.InstanceId,collector,9007199254740992,now,now,[],[],[item]);Before=new(Settings.EnvironmentId,Settings.NodeName,collector,9007199254740993,now,[new(Settings.EnvironmentId,item)],[core],[]);using(var journal=new UsageJournal(Settings,UsageRecoveryTests.Options()))await journal.SaveAsync(Before);Original=await File.ReadAllBytesAsync(Checkpoint);}
        public string[] Args()=>[Settings.LkgDirectory,Settings.EnvironmentId.ToString(),Settings.NodeName,Settings.ControlPlaneUrl.AbsoluteUri,Settings.SecretFile,SourceOwner.ToString(),TargetOwner.ToString(),Convert.ToHexStringLower(SHA256.HashData(Original))];
        public void Dispose(){if(Directory.Exists(Root))Directory.Delete(Root,true);}
    }
    private static async Task<(int Code,string Output)> RunAsync(string[] args)
    {
        var type=typeof(UsageCheckpoint).Assembly.GetType("WebApi.Gateway.CredentialUsage.UsageCloneCommand");Assert.NotNull(type);var method=type.GetMethod("RunAsync",BindingFlags.Public|BindingFlags.Static);Assert.NotNull(method);using var output=new StringWriter(CultureInfo.InvariantCulture);var code=await (Task<int>)method.Invoke(null,[args,output])!;return(code,output.ToString());
    }
    [Fact]
    public async Task Clone_quarantines_exact_old_core_and_creates_new_collector_watermark_with_gap()
    {
        using var f=new Fixture();await f.PrepareAsync();var result=await RunAsync(f.Args());Assert.Equal(0,result.Code);using var receipt=JsonDocument.Parse(result.Output);Assert.Equal("Rebound",receipt.RootElement.GetProperty("result").GetString());Assert.Equal("9007199254740993",receipt.RootElement.GetProperty("previousNextSequence").GetString());Assert.DoesNotContain(new string('s',48),result.Output);
        var material=Assert.Single(Directory.GetFiles(Path.GetDirectoryName(f.Checkpoint)!,"checkpoint.quarantine-*"));Assert.Equal(f.Original,await File.ReadAllBytesAsync(material));
        using var journal=new UsageJournal(f.Settings with{InstanceId=Guid.NewGuid()},UsageRecoveryTests.Options());var next=await journal.LoadAsync();Assert.NotNull(next);Assert.NotEqual(f.Before.CollectorId,next.CollectorId);Assert.Equal(1,next.NextSequence);Assert.Empty(next.Active);Assert.Empty(next.Pending);Assert.Contains(next.Gaps,g=>g.Reason=="OwnerClone"&&g.From<g.To);Assert.Equal(f.TargetOwner,receipt.RootElement.GetProperty("targetOwnerId").GetGuid());
    }
    [Fact]
    public async Task Repeated_offline_rebind_is_idempotent_and_keeps_the_new_collector()
    {
        using var f=new Fixture();await f.PrepareAsync();var first=await RunAsync(f.Args());Assert.Equal(0,first.Code);var bytes=await File.ReadAllBytesAsync(f.Checkpoint);var again=await RunAsync(f.Args());Assert.Equal(0,again.Code);Assert.Equal(bytes,await File.ReadAllBytesAsync(f.Checkpoint));using var receipt=JsonDocument.Parse(again.Output);Assert.Equal("AlreadyRebound",receipt.RootElement.GetProperty("result").GetString());Assert.Single(Directory.GetFiles(Path.GetDirectoryName(f.Checkpoint)!,"checkpoint.quarantine-*"));
    }
    [Theory][InlineData("same-owner")][InlineData("wrong-hash")][InlineData("wrong-environment")][InlineData("wrong-origin")]
    public async Task Unproven_clone_identity_is_refused_before_checkpoint_changes(string mismatch)
    {
        using var f=new Fixture();await f.PrepareAsync();var args=f.Args();if(mismatch=="same-owner")args[6]=args[5];if(mismatch=="wrong-hash")args[7]=new string('b',64);if(mismatch=="wrong-environment")args[1]=Guid.NewGuid().ToString();if(mismatch=="wrong-origin")args[3]="http://original-control-plane:8080/";Assert.NotEqual(0,(await RunAsync(args)).Code);Assert.Equal(f.Original,await File.ReadAllBytesAsync(f.Checkpoint));Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(f.Checkpoint)!,"checkpoint.quarantine-*"));
    }
    [Fact]
    public async Task Clone_cannot_reset_a_collector_that_has_already_recorded_new_calls()
    {
        using var f=new Fixture();await f.PrepareAsync();Assert.Equal(0,(await RunAsync(f.Args())).Code);using(var journal=new UsageJournal(f.Settings with{InstanceId=Guid.NewGuid()},UsageRecoveryTests.Options())){var c=await journal.LoadAsync();Assert.NotNull(c);await journal.SaveAsync(c with{NextSequence=2});}var current=await File.ReadAllBytesAsync(f.Checkpoint);Assert.NotEqual(0,(await RunAsync(f.Args())).Code);Assert.Equal(current,await File.ReadAllBytesAsync(f.Checkpoint));
    }
    [Theory][InlineData("environment")][InlineData("origin")]
    public async Task Repeated_clone_still_validates_the_target_node_identity(string mismatch)
    {
        using var f=new Fixture();await f.PrepareAsync();Assert.Equal(0,(await RunAsync(f.Args())).Code);var current=await File.ReadAllBytesAsync(f.Checkpoint);var args=f.Args();if(mismatch=="environment")args[1]=Guid.NewGuid().ToString();else args[3]="http://foreign-control-plane:8080/";Assert.NotEqual(0,(await RunAsync(args)).Code);Assert.Equal(current,await File.ReadAllBytesAsync(f.Checkpoint));
    }

}
