using Microsoft.EntityFrameworkCore;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Notifications;
using WebApi.Infrastructure.Alerts;
using WebApi.Infrastructure.Security;
using System.Text.Json;
namespace WebApi.NotificationFixtureHost;
// Fixture-only entry point, restricted to an exact UUID-owned QA organization.
public static class NotificationAcceptanceCommand
{
 public static bool CanSeed(string? project,string? owner,string? organizationCode)=>project is not null&&System.Text.RegularExpressions.Regex.IsMatch(project,"^webapi-enterprise-local-test-[a-f0-9-]{36}$")&&Guid.TryParseExact(project["webapi-enterprise-local-test-".Length..],"D",out var instance)&&instance!=Guid.Empty&&Guid.TryParseExact(owner,"D",out var identity)&&identity!=Guid.Empty&&organizationCode=="NOTIFY_"+identity.ToString("N").ToUpperInvariant();
 public static async Task<int> RunAsync(string ruleId)
 {
  if(!Guid.TryParseExact(ruleId,"D",out var id))return 2;var project=System.Environment.GetEnvironmentVariable("WEBAPI_RUNTIME_PROJECT");var owner=System.Environment.GetEnvironmentVariable("WEBAPI_RUNTIME_OWNER");
  if(System.Environment.GetEnvironmentVariable("WEBAPI_NOTIFICATION_QA_ENABLED")!="true")return 2;
  var builder=Host.CreateApplicationBuilder();await using var db=new WebApiDbContext(new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(DatabaseSettings.ConnectionString()).Options);var rule=await db.Set<AlertRule>().AsNoTracking().SingleOrDefaultAsync(r=>r.Id==id);if(rule is null||rule.EnvironmentId is null||rule.ProjectId is null)return 2;var code=await db.Set<Organization>().Where(o=>o.Id==rule.OrganizationId).Select(o=>o.Code).SingleAsync();if(!CanSeed(project,owner,code))return 2;
  var settings=NotificationDeploymentSettings.Read(builder.Configuration,builder.Environment);var scopes=new AlertRuleScopeResolver(db,new AuthorizationService(db));var planner=new NotificationPlanner(db,scopes,settings);
  await using var tx=await db.Database.BeginTransactionAsync();await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(8901202)");await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM alert_rules WHERE id={id} FOR UPDATE");var now=await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT now() AS \"Value\"").SingleAsync();var occurrence=1+(await db.Set<AlertEvent>().Where(e=>e.RuleId==id).MaxAsync(e=>(long?)e.OccurrenceNo)??0);var alert=new AlertEvent{RuleId=id,RuleRevision=rule.Revision,LogicRevision=rule.LogicRevision,OrganizationId=rule.OrganizationId,ProjectId=rule.ProjectId.Value,EnvironmentId=rule.EnvironmentId.Value,ResourceKey="Environment",ResourceType="Environment",OccurrenceNo=occurrence,Severity=rule.Severity,Message="Owned fixture evaluation",RuleSummary=rule.Expression,StartedAt=now,ConditionStartedAt=now,LastObservedAt=now,EvaluationState="Known"};var transition=new AlertEventTransition{EventId=alert.Id,ToStatus="Open",Reason="Triggered",OccurredAt=now};db.AddRange(alert,transition);await planner.OnTransitionAsync(alert,transition);await db.SaveChangesAsync();await tx.CommitAsync();Console.WriteLine(JsonSerializer.Serialize(new{eventId=alert.Id,transitionId=transition.Id,occurrenceNo=alert.OccurrenceNo},new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase}));return 0;
 }
}
