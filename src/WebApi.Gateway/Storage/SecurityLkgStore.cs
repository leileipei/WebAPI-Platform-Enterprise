using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Text.Json;
using System.Text.Json.Serialization;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
namespace WebApi.Gateway.Storage;
public interface ISecurityLkgStore
{
 ISecurityLkgOwner AcquireOwnership();
}
public interface ISecurityLkgOwner:IAsyncDisposable
{
 Task<SecurityNodeMaterial?> ReadAsync(CancellationToken ct=default);
 Task WriteAtomicAsync(SecurityNodeMaterial material,CancellationToken ct=default);
 Task CompleteCleanlyAsync(CancellationToken ct=default);
}
public sealed class SecurityLkgStore(string inputDirectory):ISecurityLkgStore
{
 private readonly string directory=NormalizeDirectory(inputDirectory);
 private readonly object ownerSync=new();private IDisposable? ownership;
 private const long MaximumEncodedBytes=32L*1024*1024;
 private static readonly JsonSerializerOptions Strict=new(CanonicalJson.Options){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
 public string FilePath=>Path.Combine(directory,"security.lkg");
 private static string NormalizeDirectory(string input)
 {
  var full=Path.GetFullPath(input);
  if(OperatingSystem.IsMacOS())foreach(var systemAlias in new[]{"/tmp","/var"})
  {
   if(full!=systemAlias&&!full.StartsWith(systemAlias+"/",StringComparison.Ordinal))continue;
   var target=new DirectoryInfo(systemAlias).ResolveLinkTarget(true)?.FullName;
   if(target=="/private"+systemAlias)full=target+full[systemAlias.Length..];
   break;
  }
  return full;
 }
 public ISecurityLkgOwner AcquireOwnership()
 {
  lock(ownerSync)
  {
   if(ownership is not null)throw new IOException("Security directory already has a writer.");
   CheckLinks(directory);
   if(OperatingSystem.IsWindows())Directory.CreateDirectory(directory);
   else Directory.CreateDirectory(directory,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
   CheckLinks(directory);
   if(!OperatingSystem.IsWindows())
   {
    var mode=File.GetUnixFileMode(directory);
    if((mode&(UnixFileMode.GroupRead|UnixFileMode.GroupWrite|UnixFileMode.OtherRead|UnixFileMode.OtherWrite))!=0)throw new UnauthorizedAccessException("Security directory must remain private.");
   }
   IDisposable anchor;
   if(OperatingSystem.IsLinux()||OperatingSystem.IsMacOS())
   {
    // Lock the directory inode itself. A removable lock file can be unlinked
    // and recreated while the first process still holds its old inode.
    var descriptor=open(directory,DirectoryOpenFlags());if(descriptor<0)throw new IOException("Cannot open security directory for ownership.");
    if(flock(descriptor,2|4)!=0){close(descriptor);throw new IOException("Security directory already has a writer.");}
    anchor=new UnixOwnership(descriptor);
   }
   else if(OperatingSystem.IsWindows())
   {
    var lockPath=Path.Combine(directory,".security-writer.lock");CheckLinks(lockPath);
    anchor=new FileStream(lockPath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
   }
   else throw new PlatformNotSupportedException("Security directory ownership is unsupported on this platform.");
   try{CreateRunningMarker();ownership=anchor;return new Ownership(this,anchor);}
   catch{anchor.Dispose();throw;}
  }
 }
 private void ReleaseOwnership(IDisposable anchor){lock(ownerSync){if(ReferenceEquals(ownership,anchor)){ownership=null;anchor.Dispose();}}}
 private sealed class UnixOwnership(int descriptor):IDisposable
 {
  private readonly SafeFileHandle handle=new((IntPtr)descriptor,true);
  public void Dispose()=>handle.Dispose();
 }
 private sealed class Ownership(SecurityLkgStore owner,IDisposable anchor):ISecurityLkgOwner
 {
  private readonly SemaphoreSlim operations=new(1,1);private bool released;private bool completed;
  public async Task<SecurityNodeMaterial?> ReadAsync(CancellationToken ct=default)
  {await operations.WaitAsync(ct);try{ObjectDisposedException.ThrowIf(released||completed,this);return await owner.ReadOwnedAsync(ct);}finally{operations.Release();}}
  public async Task WriteAtomicAsync(SecurityNodeMaterial material,CancellationToken ct=default)
  {await operations.WaitAsync(ct);try{ObjectDisposedException.ThrowIf(released||completed,this);await owner.WriteOwnedAsync(material,ct);}finally{operations.Release();}}
  public async Task CompleteCleanlyAsync(CancellationToken ct=default)
  {await operations.WaitAsync(ct);try{ObjectDisposedException.ThrowIf(released,this);if(completed)return;owner.RemoveRunningMarker();completed=true;}finally{operations.Release();}}
  public async ValueTask DisposeAsync()
  {await operations.WaitAsync();try{if(!released){released=true;owner.ReleaseOwnership(anchor);}}finally{operations.Release();}}
 }
 private string RunningMarkerPath=>Path.Combine(directory,".security-running");
 private void CreateRunningMarker()
 {
  // A marker is durable before this owner may observe or admit any state.
  // A prior unclean stop leaves it in place and requires controlled recovery.
  CheckLinks(RunningMarkerPath);
  using(var marker=new FileStream(RunningMarkerPath,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
  {if(!OperatingSystem.IsWindows())File.SetUnixFileMode(RunningMarkerPath,UnixFileMode.UserRead|UnixFileMode.UserWrite);marker.WriteByte(1);marker.Flush(true);}
  SyncDirectory(directory);
 }
 private void RemoveRunningMarker()
 {
  CheckLinks(RunningMarkerPath);
  if((File.GetAttributes(RunningMarkerPath)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("security_running_marker_invalid");
  File.Delete(RunningMarkerPath);SyncDirectory(directory);
 }
 private static void CheckLinks(string path)
 {
  var full=Path.GetFullPath(path);var current=Path.GetPathRoot(full)!;
  foreach(var part in full[current.Length..].Split(Path.DirectorySeparatorChar,StringSplitOptions.RemoveEmptyEntries))
  {current=Path.Combine(current,part);if(new DirectoryInfo(current).LinkTarget is not null||new FileInfo(current).LinkTarget is not null)throw new IOException("Security directory symbolic links are not allowed.");}
 }
 private async Task<SecurityNodeMaterial?> ReadOwnedAsync(CancellationToken ct=default)
 {
  try
  {
   if((File.GetAttributes(FilePath)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("security_lkg_invalid_file");
   if(!OperatingSystem.IsWindows()&&(File.GetUnixFileMode(FilePath)&(UnixFileMode.GroupRead|UnixFileMode.GroupWrite|UnixFileMode.OtherRead|UnixFileMode.OtherWrite))!=0)throw new UnauthorizedAccessException("Security LKG must remain private.");
   await using var stream=new FileStream(FilePath,FileMode.Open,FileAccess.Read,FileShare.Read,4096,FileOptions.Asynchronous);
   if(stream.Length>MaximumEncodedBytes)throw new InvalidDataException("security_lkg_invalid_file");
   using var bounded=new BoundedReadStream(stream,MaximumEncodedBytes);
   var material=await JsonSerializer.DeserializeAsync<SecurityNodeMaterial>(bounded,Strict,ct)??throw new InvalidDataException("security_lkg_empty");if(material.Checkpoint is null||material.Lease is null)throw new InvalidDataException("security_lkg_empty");return material;
  }
  catch(FileNotFoundException){return null;}
  catch(DirectoryNotFoundException){return null;}
 }
 private async Task WriteOwnedAsync(SecurityNodeMaterial material,CancellationToken ct=default)
 {
  Directory.CreateDirectory(directory);
  if((File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)throw new UnauthorizedAccessException("Security directory cannot be a link.");
  if(!OperatingSystem.IsWindows())
  {
   if((File.GetUnixFileMode(directory)&UnixFileMode.UserWrite)==0)throw new UnauthorizedAccessException("Security directory is read-only.");File.SetUnixFileMode(directory,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
  }
  var temporary=FilePath+"."+Guid.NewGuid().ToString("N")+".tmp";
  try
  {
   await using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.Asynchronous|FileOptions.WriteThrough))
   {if(!OperatingSystem.IsWindows())File.SetUnixFileMode(temporary,UnixFileMode.UserRead|UnixFileMode.UserWrite);using var bounded=new BoundedWriteStream(stream,MaximumEncodedBytes);await JsonSerializer.SerializeAsync(bounded,material,CanonicalJson.Options,ct);await stream.FlushAsync(ct);stream.Flush(true);}
   ct.ThrowIfCancellationRequested();File.Move(temporary,FilePath,true);SyncDirectory(directory);
  }
  finally{if(File.Exists(temporary))File.Delete(temporary);}
 }
 // The file may grow after its initial length check, so cap bytes actually
 // read from the already-open handle as well as its initial reported size.
 internal sealed class BoundedReadStream(Stream inner,long limit):Stream
 {
  private long consumed;
  public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;
  public override long Length=>throw new NotSupportedException();public override long Position {get=>consumed;set=>throw new NotSupportedException();}
  private int Allowed(int count)=>count==0?0:(int)Math.Min(count,limit-consumed+1);
  private int Accept(int count){if(count>limit-consumed)throw new InvalidDataException("security_lkg_size_invalid");consumed+=count;return count;}
  public override int Read(byte[] buffer,int offset,int count)=>Accept(inner.Read(buffer,offset,Allowed(count)));
  public override int Read(Span<byte> buffer)=>Accept(inner.Read(buffer[..Allowed(buffer.Length)]));
  public override Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken ct)=>ReadAsync(buffer.AsMemory(offset,count),ct).AsTask();
  public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default)=>Accept(await inner.ReadAsync(buffer[..Allowed(buffer.Length)],ct));
  public override void Flush()=>throw new NotSupportedException();public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
  public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
 }
 // Bound the actual encoded JSON, including Base64 expansion, before the
 // temporary file can replace the previously readable authoritative state.
 private sealed class BoundedWriteStream(Stream inner,long limit):Stream
 {
  private long written;
  public override bool CanRead=>false;public override bool CanSeek=>false;public override bool CanWrite=>true;
  public override long Length=>written;public override long Position {get=>written;set=>throw new NotSupportedException();}
  private void Reserve(int count){if(count<0||count>limit-written)throw new InvalidDataException("security_lkg_size_invalid");written+=count;}
  public override void Write(byte[] buffer,int offset,int count){Reserve(count);inner.Write(buffer,offset,count);}
  public override void Write(ReadOnlySpan<byte> buffer){Reserve(buffer.Length);inner.Write(buffer);}
  public override Task WriteAsync(byte[] buffer,int offset,int count,CancellationToken ct){Reserve(count);return inner.WriteAsync(buffer,offset,count,ct);}
  public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken ct=default){Reserve(buffer.Length);return inner.WriteAsync(buffer,ct);}
  public override void Flush()=>inner.Flush();public override Task FlushAsync(CancellationToken ct)=>inner.FlushAsync(ct);
  public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
  public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
  public override void SetLength(long value)=>throw new NotSupportedException();
 }
 private static void SyncDirectory(string path)
 {
  if(!OperatingSystem.IsLinux()&&!OperatingSystem.IsMacOS())return;
  var descriptor=open(path,DirectoryOpenFlags());if(descriptor<0)throw new IOException("Cannot open security directory for durability.");try{if(fsync(descriptor)!=0)throw new IOException("Cannot flush security directory.");}finally{close(descriptor);}
 }
 private static int DirectoryOpenFlags()
 {
  // O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC. Linux ARM uses different values
  // for the first two flags from x86; unsupported architectures fail closed.
  if(OperatingSystem.IsLinux())return RuntimeInformation.ProcessArchitecture switch
  {
   Architecture.Arm or Architecture.Arm64=>(1<<14)|(1<<15)|(1<<19),
   Architecture.X86 or Architecture.X64=>(1<<16)|(1<<17)|(1<<19),
   _=>throw new PlatformNotSupportedException("Unsupported Linux architecture for security directory ownership.")
  };
  if(OperatingSystem.IsMacOS())return 0x00100000|0x00000100|0x01000000;
  throw new PlatformNotSupportedException("Unsupported platform for security directory ownership.");
 }
 [DllImport("libc",SetLastError=true)]private static extern int open(string path,int flags);
 [DllImport("libc",SetLastError=true)]private static extern int flock(int descriptor,int operation);
 [DllImport("libc",SetLastError=true)]private static extern int fsync(int descriptor);
 [DllImport("libc",SetLastError=true)]private static extern int close(int descriptor);
}
