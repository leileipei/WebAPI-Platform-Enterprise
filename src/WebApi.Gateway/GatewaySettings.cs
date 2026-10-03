namespace WebApi.Gateway;
public sealed record GatewaySettings(Guid EnvironmentId,string NodeName,string SecretFile,string LkgDirectory,Uri ControlPlaneUrl,bool AutomaticUpdates,Guid InstanceId)
{
    public static GatewaySettings Read(IConfiguration config)
    {
        if(!Guid.TryParse(config["Gateway:EnvironmentId"],out var env)||env==Guid.Empty||string.IsNullOrWhiteSpace(config["Gateway:NodeName"])||string.IsNullOrWhiteSpace(config["Gateway:SecretFile"])||string.IsNullOrWhiteSpace(config["Gateway:LkgDirectory"])||!Uri.TryCreate(config["Gateway:ControlPlaneUrl"],UriKind.Absolute,out var url)||url.Scheme is not ("http" or "https")||url.UserInfo.Length>0) throw new InvalidOperationException("Gateway requires explicit environment, node name, secret file, LKG directory and control plane URL.");
        return new(env,config["Gateway:NodeName"]!,config["Gateway:SecretFile"]!,config["Gateway:LkgDirectory"]!,url,config["Gateway:AutomaticUpdates"]!="false",Guid.NewGuid());
    }
}
