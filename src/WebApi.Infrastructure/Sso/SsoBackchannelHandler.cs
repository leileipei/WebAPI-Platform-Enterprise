using System.Net.Sockets;
using WebApi.Contracts.Common;
namespace WebApi.Infrastructure.Sso;

// The same restricted transport is used for discovery, JWKS and the OIDC token request.
public sealed class SsoBackchannelHandler:DelegatingHandler
{
    public SsoBackchannelHandler(IOidcAddressPolicy policy):base(new SocketsHttpHandler
    {
        AllowAutoRedirect=false,UseCookies=false,UseProxy=false,
        ConnectCallback=async (context,ct)=>
        {
            var uri=context.InitialRequestMessage.RequestUri??throw Rejected();
            var addresses=await policy.ResolveAllowedAsync(uri,ct);
            foreach(var address in addresses)
            {
                var socket=new Socket(address.AddressFamily,SocketType.Stream,ProtocolType.Tcp);
                try {await socket.ConnectAsync(address,context.DnsEndPoint.Port,ct);return new NetworkStream(socket,true);}
                catch(OperationCanceledException){socket.Dispose();throw;}
                catch(SocketException){socket.Dispose();}
            }
            throw Rejected();
        }
    }){Policy=policy;}
    private IOidcAddressPolicy Policy {get;}
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        var token=request.Method==HttpMethod.Post;
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(token?15:10));
        try
        {
            await Policy.ResolveAllowedAsync(request.RequestUri??throw Rejected(),timeout.Token);
            var response=await base.SendAsync(request,timeout.Token);
            try
            {
                if((int)response.StatusCode is >=300 and <400) throw Rejected();
                var content=await SsoBoundedResponse.ReadAsync(response,token?128*1024:256*1024,timeout.Token);
                var replacement=new ByteArrayContent(content);
                foreach(var header in response.Content.Headers)replacement.Headers.TryAddWithoutValidation(header.Key,header.Value);
                response.Content.Dispose();response.Content=replacement;
                return response;
            }
            catch {response.Dispose();throw;}
        }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested){throw new ApiException(422,"sso_endpoint_timeout","身份服务请求超时。");}
        catch(HttpRequestException){throw Rejected();}
    }
    private static ApiException Rejected()=>new(422,"sso_endpoint_rejected","身份服务请求未通过访问策略。");
}
internal static class SsoBoundedResponse
{
    public static async Task<byte[]> ReadAsync(HttpResponseMessage response,int maximum,CancellationToken ct)
    {
        if(response.Content.Headers.ContentLength>maximum)throw TooLarge();
        await using var stream=await response.Content.ReadAsStreamAsync(ct);
        using var output=new MemoryStream();var buffer=new byte[8192];
        while(true)
        {
            var read=await stream.ReadAsync(buffer,ct);
            if(read==0)break;
            if(output.Length+read>maximum)throw TooLarge();
            output.Write(buffer,0,read);
        }
        return output.ToArray();
    }
    private static ApiException TooLarge()=>new(422,"sso_response_too_large","身份服务响应超过允许大小。");
}
