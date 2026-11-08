using System.Text.Json;
namespace WebApi.Infrastructure.CredentialSecurity;
public sealed record EnvironmentSecurityPolicy(bool RequireEnvironmentCredentials,string MinimumSnapshotSchema);
public static class SecurityPolicyRules
{
 public static EnvironmentSecurityPolicy Parse(string? json)
 {
  try{
   using var document=JsonDocument.Parse(json??"");var root=document.RootElement;
   if(root.ValueKind!=JsonValueKind.Object||root.EnumerateObject().Count()!=2||root.EnumerateObject().Select(p=>p.Name).Distinct().Count()!=2||root.EnumerateObject().Any(p=>p.Name is not ("requireEnvironmentCredentials" or "minimumSnapshotSchema")))throw new InvalidOperationException("Invalid security policy.");
   var require=root.GetProperty("requireEnvironmentCredentials").GetBoolean();var schema=root.GetProperty("minimumSnapshotSchema").GetString();
   if(schema is not ("2.0" or "2.1" or "2.2" or "2.3" or "2.4"))throw new InvalidOperationException("Invalid security policy.");return new(require,schema);
  }catch(Exception e)when(e is JsonException or KeyNotFoundException or InvalidOperationException){throw new InvalidOperationException("Invalid security policy.",e);}
 }
 // These ranks are ledger policy levels. A network-only 2.3 snapshot does not
 // become a governance 2.3 snapshot merely because its version string matches.
 private static int Rank(string schema)=>schema switch {"2.0"=>0,"2.1"=>1,"2.2"=>2,"2.3"=>3,"2.4"=>4,_=>-1};
 public static bool Strengthens(EnvironmentSecurityPolicy next,EnvironmentSecurityPolicy prior)
 {var n=Rank(next.MinimumSnapshotSchema);var p=Rank(prior.MinimumSnapshotSchema);return n>=0&&p>=0&&n>=p&&(!prior.RequireEnvironmentCredentials||next.RequireEnvironmentCredentials);}
 public static EnvironmentSecurityPolicy AtEpoch(SecurityLedgerMaterial material,long epoch)
 {
  var last=material.Events.LastOrDefault(e=>e.Epoch<=epoch&&e.EventType=="EnvironmentPolicyChanged");
  if(last is not null)return Parse(last.PolicyJson);
  if(material.InitialRequireEnvironmentCredentials is bool initial)return new(initial,"2.0");
  var first=material.Events.FirstOrDefault(e=>e.EventType=="EnvironmentPolicyChanged");
  if(first is null)return new(material.RequireEnvironmentCredentials,"2.0");
  if(!Parse(first.PolicyJson).RequireEnvironmentCredentials)return new(false,"2.0");
  throw new WebApi.Contracts.Common.ApiException(503,"security_initial_policy_unknown","初始安全策略缺少可验证记录，请完成恢复检查。");
 }
}
