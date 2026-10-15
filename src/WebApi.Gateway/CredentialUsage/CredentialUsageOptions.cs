using System.Globalization;
namespace WebApi.Gateway.CredentialUsage;
public sealed record CredentialUsageOptions(bool Enabled,int FlushIntervalSeconds,int CheckpointIntervalMs,int MaxInMemoryKeys,int MaxPendingBytes,int MaxBatchEntries,int MaxBatchBytes,int MaxPendingAgeHours)
{
    public static CredentialUsageOptions Read(IConfiguration configuration)
    {
        int Number(string name,int fallback,int min,int max)
        {
            var text=configuration["CredentialUsage:"+name];if(text is null)return fallback;
            if(!int.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out var value)||value<min||value>max)
                throw new InvalidOperationException("Invalid CredentialUsage:"+name+" deployment value.");
            return value;
        }
        var enabled=true;var raw=configuration["CredentialUsage:Enabled"];
        if(raw is not null&&!bool.TryParse(raw,out enabled))throw new InvalidOperationException("Invalid CredentialUsage:Enabled deployment value.");
        return new(enabled,Number("FlushIntervalSeconds",15,5,60),Number("CheckpointIntervalMs",1000,100,5000),
            Number("MaxInMemoryKeys",10000,100,20000),Number("MaxPendingBytes",64*1024*1024,8*1024*1024,128*1024*1024),
            Number("MaxBatchEntries",1000,1,1000),Number("MaxBatchBytes",256*1024,16*1024,256*1024),Number("MaxPendingAgeHours",24,1,24));
    }
}
