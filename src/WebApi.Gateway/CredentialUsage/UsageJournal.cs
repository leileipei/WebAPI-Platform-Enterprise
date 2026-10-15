using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using WebApi.Contracts.CredentialUsage;
using WebApi.Domain.CredentialUsage;
using WebApi.Infrastructure.Gateway;
namespace WebApi.Gateway.CredentialUsage;
public interface IUsageJournalFileSystem
{
    Task<byte[]> ReadAsync(string path,CancellationToken ct);
    Task WriteDurableAsync(string path,ReadOnlyMemory<byte> bytes,CancellationToken ct);
    void Replace(string source,string destination);
    void SyncDirectory(string path);
}
public sealed class PhysicalUsageJournalFileSystem:IUsageJournalFileSystem
{
    public Task<byte[]> ReadAsync(string path,CancellationToken ct)=>File.ReadAllBytesAsync(path,ct);
    public async Task WriteDurableAsync(string path,ReadOnlyMemory<byte> bytes,CancellationToken ct)
    {
        await using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,8192,FileOptions.Asynchronous|FileOptions.WriteThrough);
        if(!OperatingSystem.IsWindows())File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite);
        await file.WriteAsync(bytes,ct);await file.FlushAsync(ct);file.Flush(true);
    }
    public void Replace(string source,string destination)=>File.Move(source,destination,true);
    public void SyncDirectory(string path)
    {
        if(OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Usage journal requires directory durability support.");
        var handle=open(path,0);if(handle<0)throw new IOException("Cannot open usage journal directory.");
        try{if(fsync(handle)!=0)throw new IOException("Cannot flush usage journal directory.");}finally{close(handle);}
    }
    [DllImport("libc",SetLastError=true)]private static extern int open(string path,int flags);
    [DllImport("libc",SetLastError=true)]private static extern int fsync(int handle);
    [DllImport("libc",SetLastError=true)]private static extern int close(int handle);
}

public sealed class UsageJournal:IDisposable
{
    private sealed record CleanStop(Guid WriterInstanceId,string Checksum);
    private sealed record Identity(Guid EnvironmentId,string NodeName,string ControlPlane,string EnrollmentHash);
    private sealed record Envelope(int Format,Identity Identity,Guid WriterInstanceId,UsageCheckpoint Checkpoint,string Checksum);
    private static readonly JsonSerializerOptions Json=new(UsageProtocolJson.Options){MaxDepth=16};
    private const long MaximumFileBytes=128L*1024*1024+20000L*1024+2L*1024*1024;
    private readonly GatewaySettings settings;private readonly CredentialUsageOptions options;private readonly IUsageJournalFileSystem files;private readonly TimeProvider clock;
    private readonly SemaphoreSlim writer=new(1,1);private FileStream? ownership;private bool disposed;private Identity? identity;
    public Guid? LastWriterInstanceId {get;private set;}
    public bool RecoveredOrphanedWrite {get;private set;}
    public string FilePath=>Path.Combine(settings.LkgDirectory,"credential-usage","checkpoint.json");
    public UsageJournal(GatewaySettings settings,CredentialUsageOptions options,IUsageJournalFileSystem? files=null,TimeProvider? clock=null)
    {this.settings=settings;this.options=options;this.files=files??new PhysicalUsageJournalFileSystem();this.clock=clock??TimeProvider.System;}
    public async Task<UsageCheckpoint?> LoadAsync(CancellationToken ct=default)
    {
        await writer.WaitAsync(ct);try{EnsureOwner(false);var value=await ReadCoreAsync(ct);await RetainOrphanedWritesAsync(ct);return value;}finally{writer.Release();}
    }
    public async Task SaveAsync(UsageCheckpoint checkpoint,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();await writer.WaitAsync(ct);string? temporary=null;
        try
        {
            EnsureOwner(true);Validate(checkpoint);
            if(checkpoint.Active.Count>options.MaxInMemoryKeys||checkpoint.Pending.Sum(x=>(long)JsonSerializer.SerializeToUtf8Bytes(x,Json).Length)>options.MaxPendingBytes)throw new InvalidDataException("Usage checkpoint exceeds configured queue budget.");
            if(File.Exists(FilePath))
            {
                var before=await ReadCoreAsync(ct);
                await RetainOrphanedWritesAsync(ct);
                if(before is not null&&before.CollectorId!=checkpoint.CollectorId)
                {
                    var summary=new{previousCollectorId=before.CollectorId,collectorId=checkpoint.CollectorId,at=clock.GetUtcNow(),pendingBatches=before.Pending.Count,pendingHashes=before.Pending.Take(16).Select(UsageBatchRules.CoreHash).ToArray(),gaps=checkpoint.Gaps.Take(16).ToArray()};
                    await WriteControlAsync("failure-summary.json",JsonSerializer.SerializeToUtf8Bytes(summary,Json),ct);
                }
                if(before is not null&&LastWriterInstanceId!=settings.InstanceId)
                {
                    if(before.Active.Count>0&&checkpoint.Active.Count>0)throw new InvalidDataException("Seal restored active usage under its original producer before saving a new instance.");
                    var gaps=new UsageGapLedger();foreach(var gap in before.Gaps.Concat(checkpoint.Gaps))gaps.Add(gap);checkpoint=checkpoint with{Gaps=gaps.Snapshot()};
                }
            }
            else await RetainOrphanedWritesAsync(ct);
            var current=identity!;var envelope=new Envelope(1,current,settings.InstanceId,checkpoint,Checksum(current,settings.InstanceId,checkpoint));
            var bytes=JsonSerializer.SerializeToUtf8Bytes(envelope,Json);if(bytes.LongLength>MaximumFileBytes)throw new InvalidDataException("Usage checkpoint exceeds storage bound.");
            temporary=Path.Combine(Path.GetDirectoryName(FilePath)!,".checkpoint-"+Guid.NewGuid().ToString("N")+".tmp");
            await files.WriteDurableAsync(temporary,bytes,ct);ct.ThrowIfCancellationRequested();CheckLinks(FilePath);files.Replace(temporary,FilePath);files.SyncDirectory(Path.GetDirectoryName(FilePath)!);LastWriterInstanceId=settings.InstanceId;
        }
        finally{if(temporary is not null&&File.Exists(temporary))File.Delete(temporary);writer.Release();}
    }
    private async Task<UsageCheckpoint?> ReadCoreAsync(CancellationToken ct)
    {
        CheckLinks(FilePath);if(!File.Exists(FilePath))return null;
        Envelope envelope;
        try
        {
            if(new FileInfo(FilePath).Length>MaximumFileBytes)throw new InvalidDataException("Usage checkpoint is too large.");
            var bytes=await files.ReadAsync(FilePath,ct);using var document=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=16});Unique(document.RootElement);
            envelope=document.RootElement.Deserialize<Envelope>(Json)??throw new JsonException();
        }
        catch(Exception error) when(error is JsonException or InvalidDataException)
        {QuarantineCore();throw new InvalidDataException("Usage checkpoint is corrupt and has been retained.");}
        // A queue belonging to another identity is not ours to move, reset or replay.
        if(envelope.Identity!=identity)throw new InvalidDataException("Usage checkpoint belongs to another node or control plane.");
        try
        {
            if(envelope.Format!=1||envelope.WriterInstanceId==Guid.Empty||envelope.Checksum!=Checksum(envelope.Identity,envelope.WriterInstanceId,envelope.Checkpoint))throw new InvalidDataException("Usage checksum mismatch.");
            Validate(envelope.Checkpoint);
        }
        catch(Exception error) when(error is InvalidDataException or WebApi.Contracts.Common.ApiException or ArgumentException or OverflowException)
        {QuarantineCore();throw new InvalidDataException("Usage checkpoint validation failed and has been retained.");}
        LastWriterInstanceId=envelope.WriterInstanceId;var checkpoint=envelope.Checkpoint;
        if(envelope.WriterInstanceId!=settings.InstanceId)
        {
            var ledger=new UsageGapLedger();foreach(var gap in checkpoint.Gaps)ledger.Add(gap);var now=clock.GetUtcNow();
            var clean=false;var marker=Path.Combine(Path.GetDirectoryName(FilePath)!,"clean-stop.json");CheckLinks(marker);
            if(File.Exists(marker)&&new FileInfo(marker).Length<=1024)
            {
                try{var stop=JsonSerializer.Deserialize<CleanStop>(await files.ReadAsync(marker,ct),Json);clean=stop?.WriterInstanceId==envelope.WriterInstanceId&&stop.Checksum==envelope.Checksum;}catch(JsonException){}
            }
            ledger.Add(checkpoint.DurableThrough<now?new(checkpoint.DurableThrough,now,clean?"NodeStopped":"CrashTail"):new(now.AddSeconds(-1),now,"ClockSkew"));
            checkpoint=checkpoint with{Gaps=ledger.Snapshot()};
        }
        return checkpoint;
    }
    public async Task MarkCleanStopAsync(CancellationToken ct=default)
    {
        await writer.WaitAsync(ct);
        try
        {
            EnsureOwner(true);var checkpoint=await ReadCoreAsync(ct);
            if(checkpoint is null||checkpoint.Active.Count>0||LastWriterInstanceId!=settings.InstanceId)throw new InvalidDataException("Clean stop requires the current sealed durable checkpoint.");
            await WriteControlAsync("clean-stop.json",JsonSerializer.SerializeToUtf8Bytes(new CleanStop(settings.InstanceId,Checksum(identity!,settings.InstanceId,checkpoint)),Json),ct);
        }
        finally{writer.Release();}
    }
    private async Task WriteControlAsync(string name,byte[] bytes,CancellationToken ct)
    {
        if(bytes.Length>16384)throw new InvalidDataException("Usage failure summary exceeds its bound.");
        var target=Path.Combine(Path.GetDirectoryName(FilePath)!,name);CheckLinks(target);var temporary=target+"-"+Guid.NewGuid().ToString("N")+".tmp";
        try{await files.WriteDurableAsync(temporary,bytes,ct);files.Replace(temporary,target);files.SyncDirectory(Path.GetDirectoryName(FilePath)!);}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    public async Task QuarantineAsync(string reason,CancellationToken ct=default)
    {await writer.WaitAsync(ct);try{EnsureOwner(true);if(File.Exists(FilePath))QuarantineCore();LastWriterInstanceId=null;}finally{writer.Release();}}
    private void QuarantineCore()
        =>QuarantineFile(FilePath);
    private void QuarantineFile(string source)
    {
        var directory=Path.GetDirectoryName(FilePath)!;
        if(Directory.EnumerateFiles(directory,"checkpoint.quarantine-*").Take(2).Count()>=2)throw new IOException("Usage quarantine requires operator review before further recovery.");
        var target=Path.Combine(directory,"checkpoint.quarantine-"+Guid.NewGuid().ToString("N")+".json");files.Replace(source,target);files.SyncDirectory(directory);
    }
    private async Task RetainOrphanedWritesAsync(CancellationToken ct)
    {
        var directory=Path.GetDirectoryName(FilePath)!;
        var pending=Directory.EnumerateFiles(directory,"*.tmp")
            .Where(p=>System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(p),@"^(?:\.checkpoint-|clean-stop\.json-|failure-summary\.json-)[a-f0-9]{32}\.tmp$"))
            .Take(3).ToArray();
        if(pending.Length+Directory.EnumerateFiles(directory,"checkpoint.quarantine-*").Take(2).Count()>2)
            throw new IOException("Interrupted usage writes require operator review before further growth.");
        foreach(var file in pending)
        {
            ct.ThrowIfCancellationRequested();CheckLinks(file);
            if(new FileInfo(file).Length>MaximumFileBytes)throw new InvalidDataException("Interrupted usage write exceeds storage bound.");
            Identity? owner=null;
            try
            {
                using var document=JsonDocument.Parse(await files.ReadAsync(file,ct),new JsonDocumentOptions{MaxDepth=16});Unique(document.RootElement);
                if(document.RootElement.ValueKind==JsonValueKind.Object&&document.RootElement.TryGetProperty("identity",out var element))owner=element.Deserialize<Identity>(Json);
            }
            catch(Exception error) when(error is JsonException or InvalidDataException){}
            // A readable foreign header is never ours to move, even if other fields are damaged.
            if(owner is not null&&owner!=identity)throw new InvalidDataException("Interrupted usage write belongs to another node or control plane.");
            // The temporary state may have been written before or after an ACK. Retain it
            // byte-for-byte; only the committed checkpoint may supply future increments.
            QuarantineFile(file);RecoveredOrphanedWrite=true;
        }
    }
    private void EnsureOwner(bool writing)
    {
        ObjectDisposedException.ThrowIf(disposed,this);CheckLinks(settings.LkgDirectory);CheckLinks(Path.GetDirectoryName(FilePath)!);CheckLinks(settings.SecretFile);
        Directory.CreateDirectory(settings.LkgDirectory);var directory=Path.GetDirectoryName(FilePath)!;
        if(!Directory.Exists(directory)){Directory.CreateDirectory(directory);if(!OperatingSystem.IsWindows())File.SetUnixFileMode(directory,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);files.SyncDirectory(settings.LkgDirectory);}
        if(!OperatingSystem.IsWindows())
        {
            var mode=File.GetUnixFileMode(directory);if((mode&(UnixFileMode.GroupWrite|UnixFileMode.OtherWrite|UnixFileMode.GroupRead|UnixFileMode.OtherRead))!=0||writing&&(mode&UnixFileMode.UserWrite)==0)throw new UnauthorizedAccessException("Usage journal directory must be private and writable.");
        }
        if(ownership is null)
        {
            var lockPath=Path.Combine(directory,".writer.lock");CheckLinks(lockPath);ownership=new(lockPath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            if(!OperatingSystem.IsWindows())File.SetUnixFileMode(lockPath,UnixFileMode.UserRead|UnixFileMode.UserWrite);
            var secret=File.ReadAllText(settings.SecretFile).Trim();if(secret.Length is <32 or >1024)throw new InvalidDataException("Invalid usage node enrollment material.");
            identity=new(settings.EnvironmentId,settings.NodeName,settings.ControlPlaneUrl.AbsoluteUri.TrimEnd('/'),NodeIdentityService.Hash(secret));
        }
    }
    private void Validate(UsageCheckpoint value)
    {
        if(value is null||value.EnvironmentId!=settings.EnvironmentId||value.NodeName!=settings.NodeName||value.CollectorId==Guid.Empty||value.NextSequence<1||value.Active is null||value.Pending is null||value.Gaps is null||value.Active.Count>20000)throw new InvalidDataException("Invalid usage checkpoint.");
        if(value.Active.Any(x=>x.EnvironmentId!=value.EnvironmentId||x.Item.Count<1||x.Item.FirstObservedAt>x.Item.LastObservedAt))throw new InvalidDataException("Invalid active usage delta.");
        if(JsonSerializer.SerializeToUtf8Bytes(value.Gaps,Json).Length>1024*1024)throw new InvalidDataException("Usage gap control budget exceeded.");
        long previous=0;foreach(var batch in value.Pending)
        {
            if(batch.CollectorId!=value.CollectorId||batch.Sequence<=previous||batch.Sequence>=value.NextSequence)throw new InvalidDataException("Invalid pending usage sequence.");previous=batch.Sequence;
            UsageBatchRules.Parse(JsonSerializer.SerializeToUtf8Bytes(new UsageBatchRequest(batch.ProducerInstanceId,batch),UsageProtocolJson.Options),batch.SealedAt,24);
        }
    }
    private static string Checksum(Identity identity,Guid writer,UsageCheckpoint checkpoint)=>Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{format=1,identity,writerInstanceId=writer,checkpoint},Json)));
    private static void CheckLinks(string path)
    {
        var full=Path.GetFullPath(path);var current=Path.GetPathRoot(full)!;
        foreach(var part in full[current.Length..].Split(Path.DirectorySeparatorChar,StringSplitOptions.RemoveEmptyEntries))
        {current=Path.Combine(current,part);if(new DirectoryInfo(current).LinkTarget is not null||new FileInfo(current).LinkTarget is not null)throw new IOException("Usage journal symbolic links are not allowed.");}
    }
    private static void Unique(JsonElement value)
    {
        if(value.ValueKind==JsonValueKind.Object){var keys=new HashSet<string>(StringComparer.Ordinal);foreach(var p in value.EnumerateObject()){if(!keys.Add(p.Name))throw new JsonException();Unique(p.Value);}}
        else if(value.ValueKind==JsonValueKind.Array)foreach(var item in value.EnumerateArray())Unique(item);
    }
    public void Dispose(){if(disposed)return;disposed=true;ownership?.Dispose();writer.Dispose();}
}
