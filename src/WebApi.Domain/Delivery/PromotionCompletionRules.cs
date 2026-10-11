namespace WebApi.Domain.Delivery;
public sealed record ProductionEvidenceFact(string Type,string Result,bool Independent,bool Current,DateTimeOffset ExpiresAt);
public static class PromotionCompletionRules
{
 public static readonly IReadOnlyList<string> RequiredTypes=Array.AsReadOnly(new[]{"EntryConnectivity","AuthenticationAuthorization","CriticalBusinessCall"});
 public static string Evaluate(IReadOnlyList<ProductionEvidenceFact> latest,DateTimeOffset now)
 {
  if(latest.Any(v=>v.Current&&v.Result=="Failed"))return "VerificationFailed";
  return RequiredTypes.All(type=>latest.Any(v=>v.Type==type&&v.Result=="Passed"&&v.Current&&v.Independent&&v.ExpiresAt>now))?"Completed":"Verifying";
 }
}
