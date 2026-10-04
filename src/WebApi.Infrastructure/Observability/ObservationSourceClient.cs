using System.Net.Http.Headers;
using System.Text.Json;
namespace WebApi.Infrastructure.Observability;
public sealed class ObservationSourceClient(IHttpClientFactory factory,ObservationSourceSettings settings)
{
    public async Task<JsonDocument> GetAsync(Uri endpoint,string path,IReadOnlyDictionary<string,string> query,CancellationToken ct)
    {
        var sourceType=path.StartsWith("loki/",StringComparison.Ordinal)?"logs":path.StartsWith("api/traces",StringComparison.Ordinal)||path.StartsWith("api/v2/traces",StringComparison.Ordinal)||path.StartsWith("api/search",StringComparison.Ordinal)||path.StartsWith("api/v2/search",StringComparison.Ordinal)?"traces":"metrics";
        if(!settings.Enabled)throw ObservationSourceSettings.Unavailable(sourceType);
        var uri=new Uri(endpoint.AbsoluteUri.TrimEnd('/')+"/"+path.TrimStart('/')+"?"+string.Join('&',query.Select(x=>Uri.EscapeDataString(x.Key)+"="+Uri.EscapeDataString(x.Value))));
        try
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,uri);
            if(settings.CredentialSecretFile is {Length:>0})request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",File.ReadAllText(settings.CredentialSecretFile).Trim());
            var client=factory.CreateClient("observability");using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);response.EnsureSuccessStatusCode();
            const int maximum=8*1024*1024;if(response.Content.Headers.ContentLength>maximum)throw ObservationSourceSettings.Unavailable(sourceType);
            await using var input=await response.Content.ReadAsStreamAsync(ct);using var bytes=new MemoryStream();var buffer=new byte[8192];
            while(true){var count=await input.ReadAsync(buffer,ct);if(count==0)break;if(bytes.Length+count>maximum)throw ObservationSourceSettings.Unavailable(sourceType);bytes.Write(buffer,0,count);}
            return JsonDocument.Parse(bytes.ToArray());
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception){throw ObservationSourceSettings.Unavailable(sourceType);}
    }
}
