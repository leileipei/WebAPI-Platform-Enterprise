using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using WebApi.Contracts.CredentialUsage;
namespace WebApi.Gateway.CredentialUsage;

/// <summary>Offline command. The maintenance runner must additionally prove target-volume
/// ownership and execute with no network before any restored gateway starts.</summary>
public static class UsageCloneCommand
{
    private sealed record Receipt(int SchemaVersion,string Result,Guid SourceOwnerId,Guid TargetOwnerId,
        Guid PreviousCollectorId,string PreviousNextSequence,Guid CollectorId,string NextSequence,
        string PreviousCheckpointHash,string CheckpointHash,int QuarantinedBatches,int QuarantinedActiveKeys,DateTimeOffset At);
    private static readonly JsonSerializerOptions Json=new(UsageProtocolJson.Options){MaxDepth=16};
    public static async Task<int> RunAsync(string[] args,TextWriter output)
    {
        try
        {
            if(args.Length!=8||!Guid.TryParse(args[1],out var environment)||environment==Guid.Empty||
                !Guid.TryParse(args[5],out var sourceOwner)||sourceOwner==Guid.Empty||
                !Guid.TryParse(args[6],out var targetOwner)||targetOwner==Guid.Empty||sourceOwner==targetOwner||
                args[7].Length!=64||args[7].Any(c=>c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))||
                !Uri.TryCreate(args[3],UriKind.Absolute,out var controlPlane)||controlPlane.Scheme is not ("http" or "https")||
                controlPlane.UserInfo.Length>0||controlPlane.Query.Length>0||controlPlane.Fragment.Length>0||
                string.IsNullOrWhiteSpace(args[2]))throw new InvalidDataException();
            var directory=Path.Combine(Path.GetFullPath(args[0]),"credential-usage");
            var file=Path.Combine(directory,"checkpoint.json");var receiptFile=Path.Combine(directory,"restore-rebind.json");
            CheckLinks(args[0]);CheckLinks(args[4]);CheckLinks(file);CheckLinks(receiptFile);
            if(!File.Exists(file)||new FileInfo(file).Length>150L*1024*1024)throw new InvalidDataException();
            var settings=new GatewaySettings(environment,args[2],args[4],args[0],controlPlane,false,Guid.NewGuid());
            var options=new CredentialUsageOptions(true,15,1000,20000,128*1024*1024,1000,256*1024,24);
            using var journal=new UsageJournal(settings,options);
            var original=await File.ReadAllBytesAsync(file);var originalHash=Hash(original);
            if(File.Exists(receiptFile))
            {
                if(new FileInfo(receiptFile).Length>16384)throw new InvalidDataException();
                var saved=JsonSerializer.Deserialize<Receipt>(await File.ReadAllBytesAsync(receiptFile),Json)??throw new InvalidDataException();
                if(saved.SchemaVersion!=1||saved.SourceOwnerId!=sourceOwner||saved.TargetOwnerId!=targetOwner||
                    saved.PreviousCheckpointHash!=args[7]||saved.CheckpointHash!=originalHash)throw new InvalidDataException();
                _=await journal.LoadAsync()??throw new InvalidDataException();
                await output.WriteLineAsync(JsonSerializer.Serialize(saved with{Result="AlreadyRebound"},Json));return 0;
            }
            if(originalHash!=args[7])throw new InvalidDataException();
            var before=await journal.LoadAsync()??throw new InvalidDataException();
            // Load holds the original writer lock. A changed checkpoint or another writer
            // cannot be silently adopted between the archive check and quarantine.
            if(Hash(await File.ReadAllBytesAsync(file))!=originalHash)throw new InvalidDataException();
            var now=DateTimeOffset.UtcNow;var from=before.Pending.SelectMany(b=>b.Items.Select(i=>i.FirstObservedAt).Concat(b.Intervals.Select(i=>i.From)))
                .Concat(before.Active.Select(a=>a.Item.FirstObservedAt)).Append(before.DurableThrough).Min();
            if(from<now.AddHours(-24))from=now.AddHours(-24);if(from>=now)from=now.AddTicks(-1);
            await journal.QuarantineAsync("OwnerClone");
            var fresh=new UsageCheckpoint(environment,settings.NodeName,Guid.NewGuid(),1,now,[],[],[new(from,now,"OwnerClone")]);
            await journal.SaveAsync(fresh);
            var receipt=new Receipt(1,"Rebound",sourceOwner,targetOwner,before.CollectorId,before.NextSequence.ToString(CultureInfo.InvariantCulture),
                fresh.CollectorId,"1",originalHash,Hash(await File.ReadAllBytesAsync(file)),before.Pending.Count,before.Active.Count,now);
            var bytes=JsonSerializer.SerializeToUtf8Bytes(receipt,Json);var temporary=receiptFile+"-"+Guid.NewGuid().ToString("N")+".tmp";
            var physical=new PhysicalUsageJournalFileSystem();
            try{await physical.WriteDurableAsync(temporary,bytes,default);physical.Replace(temporary,receiptFile);physical.SyncDirectory(directory);}
            finally{if(File.Exists(temporary))File.Delete(temporary);}
            await output.WriteLineAsync(JsonSerializer.Serialize(receipt,Json));return 0;
        }
        catch(Exception error) when(error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or WebApi.Contracts.Common.ApiException)
        {
            // No password, bearer, identity hash or input path is printed on failure.
            await output.WriteLineAsync("{\"result\":\"Refused\",\"code\":\"usage_clone_rebind_refused\"}");return 3;
        }
    }
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void CheckLinks(string path)
    {
        var full=Path.GetFullPath(path);var current=Path.GetPathRoot(full)!;
        foreach(var part in full[current.Length..].Split(Path.DirectorySeparatorChar,StringSplitOptions.RemoveEmptyEntries))
        {current=Path.Combine(current,part);if(new DirectoryInfo(current).LinkTarget is not null||new FileInfo(current).LinkTarget is not null)throw new IOException();}
    }
}
