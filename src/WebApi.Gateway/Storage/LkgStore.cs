using System.Runtime.InteropServices;
using System.Text.Json;
using WebApi.Contracts.Common;
using WebApi.Contracts.Runtime;
using WebApi.Infrastructure.Runtime;
namespace WebApi.Gateway.Storage;
public sealed class LkgStore(GatewaySettings settings)
{
    public string FilePath=>Path.Combine(settings.LkgDirectory,"runtime.lkg");
    public async Task<DesiredConfigResponse?> ReadAsync(CancellationToken ct=default)
    {return (await ReadCandidatesAsync(ct)).FirstOrDefault();}
    public async Task<IReadOnlyList<DesiredConfigResponse>> ReadCandidatesAsync(CancellationToken ct=default)
    {var candidates=new List<DesiredConfigResponse>();foreach(var file in new[]{FilePath,FilePath+".previous"}) {try {if(!File.Exists(file)||new FileInfo(file).Length>32*1024*1024) continue;var value=JsonSerializer.Deserialize<DesiredConfigResponse>(await File.ReadAllBytesAsync(file,ct),CanonicalJson.Options)!;RedisSnapshotStore.Validate(settings.EnvironmentId,value.Envelope,value.Payload);candidates.Add(value);}catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or WebApi.Contracts.Common.ApiException or ArgumentException or NullReferenceException) {}}return candidates;}
    public Task WriteAtomicAsync(SnapshotEnvelope envelope,ReadOnlyMemory<byte> payload,CancellationToken ct=default)=>WriteAsync(new(envelope,payload.ToArray()),true,ct);
    public async Task RestoreAsync(DesiredConfigResponse? prior,CancellationToken ct=default)
    {if(prior is null) {File.Delete(FilePath);SyncDirectory(settings.LkgDirectory);}else await WriteAsync(prior,false,ct);}
    private async Task WriteAsync(DesiredConfigResponse value,bool rotate,CancellationToken ct)
    {
        RedisSnapshotStore.Validate(settings.EnvironmentId,value.Envelope,value.Payload);Directory.CreateDirectory(settings.LkgDirectory);
        if(!OperatingSystem.IsWindows()&&(File.GetUnixFileMode(settings.LkgDirectory)&(UnixFileMode.UserWrite|UnixFileMode.GroupWrite|UnixFileMode.OtherWrite))==0) throw new UnauthorizedAccessException("LKG directory is explicitly read-only.");
        var temporary=FilePath+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            await using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.Asynchronous|FileOptions.WriteThrough)) {if(!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary,UnixFileMode.UserRead|UnixFileMode.UserWrite);await JsonSerializer.SerializeAsync(stream,value,CanonicalJson.Options,ct);await stream.FlushAsync(ct);stream.Flush(true);}
            ct.ThrowIfCancellationRequested();if(rotate&&File.Exists(FilePath)) {var prior=await ReadAsync(ct);if(prior is not null) {var backup=FilePath+".previous.tmp";await using(var stream=new FileStream(backup,FileMode.Create,FileAccess.Write,FileShare.None)) {await JsonSerializer.SerializeAsync(stream,prior,CanonicalJson.Options,ct);stream.Flush(true);}if(!OperatingSystem.IsWindows()) File.SetUnixFileMode(backup,UnixFileMode.UserRead|UnixFileMode.UserWrite);File.Move(backup,FilePath+".previous",true);}}
            File.Move(temporary,FilePath,true);SyncDirectory(settings.LkgDirectory);
        }
        finally {if(File.Exists(temporary)) File.Delete(temporary);}
    }
    private static void SyncDirectory(string path)
    {if(!OperatingSystem.IsLinux()) return;var descriptor=open(path,0);if(descriptor<0) throw new IOException("Cannot open LKG directory for durability.");try {if(fsync(descriptor)!=0) throw new IOException("Cannot flush LKG directory.");}finally {close(descriptor);}}
    [DllImport("libc",SetLastError=true)] private static extern int open(string path,int flags);
    [DllImport("libc",SetLastError=true)] private static extern int fsync(int descriptor);
    [DllImport("libc",SetLastError=true)] private static extern int close(int descriptor);
}
