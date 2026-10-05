namespace WebApi.Infrastructure.Sso;
public sealed class SsoOptions
{
    public string PublicBaseUrl {get;set;}="";
    public string[] AllowedOrigins {get;set;}=[];
    public string[] AllowedPrivateCidrs {get;set;}=[];
    public Dictionary<string,string> SecretFiles {get;set;}=new(StringComparer.Ordinal);
    public string[] AllowedSigningAlgorithms {get;set;}=["RS256","PS256","ES256"];
    public bool FixtureEnabled {get;set;}
    public int MaxCachedSchemes {get;set;}=128;
    public Dictionary<string,string> FixtureConnectOverrides {get;set;}=new(StringComparer.Ordinal);
}
