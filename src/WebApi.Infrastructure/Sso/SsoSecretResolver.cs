using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using Microsoft.Extensions.Options;
using WebApi.Contracts.Common;
namespace WebApi.Infrastructure.Sso;
public interface ISsoSecretResolver {Task<string> ResolveAsync(string reference,CancellationToken ct=default);}
public sealed class SsoSecretResolver(IOptions<SsoOptions> options):ISsoSecretResolver
{
    public async Task<string> ResolveAsync(string reference,CancellationToken ct=default)
    {
        if(!Regex.IsMatch(reference??"", "^file://sso/[A-Za-z][A-Za-z0-9_.-]{0,127}$") ||
           !options.Value.SecretFiles.TryGetValue(reference![11..],out var path) || !Path.IsPathFullyQualified(path))
            throw Unavailable();
        try
        {
            // Deployment mappings are administrator owned. Reject symlinks at every path component.
            var info=new FileInfo(path);
            if(!info.Exists||info.LinkTarget is not null) throw Unavailable();
            for(var directory=info.Directory;directory is not null;directory=directory.Parent)
                if(directory.LinkTarget is not null) throw Unavailable();
            using var handle=OpenReadOnly(path);
            if(!OperatingSystem.IsWindows())
            {
                var mode=File.GetUnixFileMode(handle);
                if((mode&(UnixFileMode.GroupRead|UnixFileMode.GroupWrite|UnixFileMode.GroupExecute|
                    UnixFileMode.OtherRead|UnixFileMode.OtherWrite|UnixFileMode.OtherExecute))!=0) throw Unavailable();
            }
            await using var stream=new FileStream(handle,FileAccess.Read);
            if(stream.Length is <1 or >8192) throw Unavailable();
            var bytes=new byte[8193];var length=0;
            while(length<bytes.Length)
            {
                var read=await stream.ReadAsync(bytes.AsMemory(length),ct);
                if(read==0) break;
                length+=read;
            }
            if(length>8192) throw Unavailable();
            var secret=new UTF8Encoding(false,true).GetString(bytes,0,length).TrimEnd('\r','\n');
            if(string.IsNullOrWhiteSpace(secret)||secret.Any(char.IsControl)) throw Unavailable();
            return secret;
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch(ApiException){throw;}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or DecoderFallbackException or ArgumentException)
        {throw Unavailable();}
    }
    private static SafeFileHandle OpenReadOnly(string path)
    {
        if(OperatingSystem.IsWindows()) return File.OpenHandle(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        // O_NOFOLLOW closes the leaf-file replacement race between inspection and open.
        var fd=Open(path,OperatingSystem.IsMacOS()?0x100:0x20000);
        if(fd<0) throw Unavailable();
        return new SafeFileHandle(new IntPtr(fd),true);
    }
    [DllImport("libc",EntryPoint="open",SetLastError=true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path,int flags);
    private static ApiException Unavailable()=>new(422,"sso_secret_unavailable","SSO 密钥引用不可用，请检查部署映射。");
}
