using System.Net;
using System.Text.Json;
using WebApi.Contracts.CredentialUsage;
using WebApi.Gateway.Workers;
namespace WebApi.Gateway.CredentialUsage;

public sealed class UsageTransportException(int status,string code,long? expected=null):Exception("Credential usage delivery was rejected.")
{public int Status {get;}=status;public string Code {get;}=code;public long? Expected {get;}=expected;}

public sealed class UsageNodeTransport(NodeClient node)
{
    public Task<UsageReceipt> BindAsync(UsageCollectorRequest request,CancellationToken ct)=>PostAsync("collectors",request,ct);
    public Task<UsageReceipt> SendAsync(UsageBatchRequest request,CancellationToken ct)=>PostAsync("batches",request,ct);
    private async Task<UsageReceipt> PostAsync<T>(string resource,T request,CancellationToken ct)
    {
        using var response=await node.PostUsageAsync(resource,request,ct);
        // Bound responses from an older/misconfigured control plane before parsing them.
        using var input=await response.Content.ReadAsStreamAsync(ct);using var body=new MemoryStream();var buffer=new byte[4096];
        while(true){var count=await input.ReadAsync(buffer,ct);if(count==0)break;if(body.Length+count>16384)throw new InvalidDataException("Usage receipt exceeds its bound.");body.Write(buffer,0,count);}
        if(!response.IsSuccessStatusCode)
        {
            long? expected=null;var code="usage_delivery_rejected";
            try{using var problem=JsonDocument.Parse(body.ToArray());if(problem.RootElement.TryGetProperty("nextExpectedSequence",out var cursor)&&cursor.TryGetInt64(out var n)&&n>0)expected=n;if(problem.RootElement.TryGetProperty("code",out var value)&&value.GetString() is {Length:>0 and <=80} text&&text.All(c=>char.IsAsciiLetterOrDigit(c)||c=='_'))code=text;}catch(JsonException){}
            throw new UsageTransportException((int)response.StatusCode,code,expected);
        }
        var receipt=JsonSerializer.Deserialize<UsageReceipt>(body.ToArray(),UsageProtocolJson.Options)??throw new InvalidDataException("Missing usage receipt.");
        if(receipt.NextExpectedSequence<1||receipt.Result is not ("Registered" or "Applied" or "AlreadyApplied")||resource=="collectors"&&receipt.Result!="Registered"||resource=="batches"&&receipt.Result=="Registered")throw new InvalidDataException("Invalid usage receipt.");
        return receipt;
    }
}
