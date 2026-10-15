using System.Globalization;
using Microsoft.Extensions.Configuration;
namespace WebApi.Infrastructure.CredentialUsage;
public sealed record CredentialUsageRetentionOptions(int RetentionDays,int BatchHashHours,int IngestConcurrency,int IngestTimeoutSeconds,int MaxPendingAgeHours=24)
{
    public static CredentialUsageRetentionOptions Read(IConfiguration configuration)
    {
        int Number(string name,int fallback,int min,int max)
        {
            var text=configuration["CredentialUsage:"+name];if(text is null)return fallback;
            if(!int.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out var value)||value<min||value>max)
                throw new InvalidOperationException("Invalid CredentialUsage:"+name+" deployment value.");
            return value;
        }
        var retention=Number("RetentionDays",30,1,90);var hashes=Number("BatchHashHours",25,2,168);var pending=Number("MaxPendingAgeHours",24,1,24);
        if(hashes<pending+1)throw new InvalidOperationException("Credential usage batch hash retention must exceed maximum pending age by at least one hour.");
        return new(retention,hashes,Number("IngestConcurrency",2,1,4),Number("IngestTimeoutSeconds",10,1,10),pending);
    }
}
