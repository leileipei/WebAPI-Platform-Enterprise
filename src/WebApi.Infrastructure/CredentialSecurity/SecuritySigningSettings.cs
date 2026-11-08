using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;
using WebApi.Domain.CredentialSecurity;
using WebApi.Contracts.Common;
namespace WebApi.Infrastructure.CredentialSecurity;
public sealed record SecuritySigningSettings(string? KeyId,string? PrivateKeyFile,IReadOnlyDictionary<string,string>? TrustedPublicKeys=null)
{
 public static SecuritySigningSettings Read(IConfiguration config)=>new(config["Security:Signing:KeyId"],config["Security:Signing:PrivateKeyFile"],config.GetSection("Security:TrustedKeys").GetChildren().ToDictionary(x=>x.Key,x=>x.Value??""));
 public SecurityCheckpointSigner LoadSigner()
 {
  try
  {
   if(string.IsNullOrWhiteSpace(KeyId)||string.IsNullOrWhiteSpace(PrivateKeyFile))throw new IOException();
   if(!OperatingSystem.IsWindows()&&(File.GetUnixFileMode(PrivateKeyFile)&(UnixFileMode.GroupRead|UnixFileMode.GroupWrite|UnixFileMode.OtherRead|UnixFileMode.OtherWrite))!=0)throw new UnauthorizedAccessException();
   return new(KeyId,File.ReadAllText(PrivateKeyFile));
  }
  catch(Exception error)when(error is IOException or UnauthorizedAccessException){throw new ApiException(503,"security_signing_unavailable","安全签发暂不可用。");}
 }
 public TrustedSigningKeys LoadTrustedKeys()
 {
  var keys=new Dictionary<string,string>(TrustedPublicKeys??new Dictionary<string,string>(),StringComparer.Ordinal);
  if(!string.IsNullOrWhiteSpace(KeyId)&&!string.IsNullOrWhiteSpace(PrivateKeyFile))
  {
   string current;try{current=LoadSigner().ExportPublicKey();}catch(Exception error)when(error is CryptographicException or ArgumentException or InvalidDataException){throw new ApiException(503,"security_signing_unavailable","安全验证暂不可用。");}
   if(keys.TryGetValue(KeyId,out var configured)&&configured!=current)throw new ApiException(503,"security_signing_key_conflict","签名公钥配置不一致。");keys[KeyId]=current;
  }
  if(keys.Count==0)throw new ApiException(503,"security_verification_unavailable","安全验证暂不可用。");return new(keys);
 }
}
