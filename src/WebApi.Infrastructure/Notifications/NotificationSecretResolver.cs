using WebApi.Contracts.Notifications;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using WebApi.Contracts.Common;
namespace WebApi.Infrastructure.Notifications;
public interface INotificationSecretResolver {Task<NotificationSecret> ResolveAsync(string reference,NotificationChannel channel,CancellationToken ct=default);}
public sealed class NotificationSecret
{
    internal string? Username {get;}
    internal string? Password {get;}
    internal byte[]? Key {get;}
    internal NotificationSecret(string? username=null,string? password=null,byte[]? key=null){Username=username;Password=password;Key=key;}
}
public sealed class NotificationSecretResolver(NotificationDeploymentSettings settings):INotificationSecretResolver
{
    public async Task<NotificationSecret> ResolveAsync(string reference,NotificationChannel channel,CancellationToken ct=default)
    {
        if(reference is null||!settings.SecretFiles.TryGetValue(reference,out var path))throw Unavailable();
        try
        {
            var info=new FileInfo(path);if(!info.Exists||info.LinkTarget is not null)throw Unavailable();
            for(var directory=info.Directory;directory is not null;directory=directory.Parent)if(directory.LinkTarget is not null)throw Unavailable();
            using var handle=OpenReadOnly(path);
            if(!OperatingSystem.IsWindows())
            {
                var mode=File.GetUnixFileMode(handle);
                if((mode&UnixFileMode.UserRead)==0||(mode&(UnixFileMode.GroupRead|UnixFileMode.GroupWrite|UnixFileMode.GroupExecute|UnixFileMode.OtherRead|UnixFileMode.OtherWrite|UnixFileMode.OtherExecute))!=0)throw Unavailable();
            }
            await using var stream=new FileStream(handle,FileAccess.Read);if(stream.Length is <1 or >16384)throw Unavailable();
            var buffer=new byte[16385];var length=0;
            while(length<buffer.Length){var count=await stream.ReadAsync(buffer.AsMemory(length),ct);if(count==0)break;length+=count;}
            if(length is <1 or >16384)throw Unavailable();
            using var document=JsonDocument.Parse(new UTF8Encoding(false,true).GetString(buffer,0,length),new JsonDocumentOptions{MaxDepth=2});
            var values=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(var field in document.RootElement.EnumerateObject())if(field.Value.ValueKind!=JsonValueKind.String||!values.TryAdd(field.Name,field.Value.GetString()!))throw Unavailable();
            if(channel==NotificationChannel.Email)
            {
                if(values.Count!=2||!values.TryGetValue("username",out var username)||!values.TryGetValue("password",out var password)||username.Length is <1 or >320||password.Length is <1 or >4096||username.Any(c=>c is '\0' or '\r' or '\n')||password.Any(c=>c is '\0' or '\r' or '\n'))throw Unavailable();
                return new(username,password);
            }
            if(channel!=NotificationChannel.Webhook||values.Count!=1||!values.TryGetValue("keyBase64",out var encoded))throw Unavailable();
            var key=Convert.FromBase64String(encoded);if(key.Length is <32 or >64)throw Unavailable();return new(key:key);
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(ApiException){throw;}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or JsonException or DecoderFallbackException or ArgumentException or FormatException or InvalidOperationException){throw Unavailable();}
    }
    private static SafeFileHandle OpenReadOnly(string path)
    {
        if(OperatingSystem.IsWindows())return File.OpenHandle(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        var fd=Open(path,OperatingSystem.IsMacOS()?0x100:0x20000);if(fd<0)throw Unavailable();return new(new IntPtr(fd),true);
    }
    [DllImport("libc",EntryPoint="open",SetLastError=true)] private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)]string path,int flags);
    private static ApiException Unavailable()=>new(422,"notification_secret_unavailable","通知秘密引用不可用，请检查部署映射。");
}
