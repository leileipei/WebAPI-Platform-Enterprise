using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Applications;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Releases;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Applications;

public sealed class CredentialImpactService(WebApiDbContext db,AuthorizationService auth,ScopeResolver scopes,CollectionAuthorizationQuery visibility,HistoricalSnapshotService history,TimeProvider clock)
{
    private sealed record Window(DateTimeOffset From,DateTimeOffset To);
    private sealed record Impact(CredentialImpactDto Dto,bool RequiresConfirmation);
    public async Task<CredentialImpactDto> PreviewAsync(Guid credentialId,CredentialImpactRequest request,string? ifMatch,ActorContext actor,CancellationToken ct=default)
    {
        await using var transaction=await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(8901202)",ct);
        var result=await ComputeAsync(credentialId,request,ifMatch,true,actor,ct);await transaction.CommitAsync(ct);return result.Dto;
    }
    public async Task RequireConfirmationAsync(Guid credentialId,CredentialImpactRequest request,CredentialImpactConfirmation? confirmation,ActorContext actor,CancellationToken ct=default)
    {
        if(db.Database.CurrentTransaction is null)throw new InvalidOperationException("Credential impact confirmation requires the protected command transaction.");
        var result=await ComputeAsync(credentialId,request,null,false,actor,ct);
        if(confirmation is not null&&!string.Equals(confirmation.ImpactHash,result.Dto.ImpactHash,StringComparison.Ordinal))throw new ApiException(409,"credential_impact_changed","凭证影响已变化，请重新查看影响预览并确认。");
        if(result.RequiresConfirmation&&(confirmation is null||!confirmation.ConfirmCloseAllAccess))throw new ApiException(409,"credential_impact_confirmation_required","此操作可能造成凭证访问中断，请通过凭证影响预览明确确认风险。");
    }
    private async Task<Impact> ComputeAsync(Guid credentialId,CredentialImpactRequest request,string? tag,bool requireTag,ActorContext actor,CancellationToken ct)
    {
        var target=await db.Set<ApplicationCredential>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==credentialId,ct)??throw ScopeResolver.Missing();var scope=await scopes.ApplicationAsync(target.ApplicationId,ct);
        await auth.RequireAsync(actor,"credential.manage",new("credential",credentialId,scope),ct);
        if(requireTag)RevisionTag.Require(tag,target.Revision);
        if(request.ValidFrom==DateTimeOffset.MinValue||request.ExpiresAt<=request.ValidFrom)throw new ApiException(422,"invalid_validity_window","有效期结束必须晚于开始时间。");
        if(request.Status is not ("Active" or "Revoked")||target.Status=="Revoked"&&request.Status!="Revoked")throw new ApiException(409,"credential_revoked","已撤销凭证不能重新激活。");
        request=request with{ValidFrom=request.ValidFrom.ToUniversalTime(),ExpiresAt=request.ExpiresAt.ToUniversalTime()};var now=clock.GetUtcNow().ToUniversalTime();
        var app=await db.Set<ApplicationRecord>().AsNoTracking().SingleAsync(x=>x.Id==target.ApplicationId,ct);
        var grants=await db.Set<ApplicationApiPermission>().AsNoTracking().Where(x=>x.ApplicationId==app.Id).OrderBy(x=>x.Id).Select(x=>new{x.Id,x.Revision,x.EnvironmentId,x.ApiId,x.ValidFrom,x.ExpiresAt}).ToArrayAsync(ct);
        var authorized=visibility.Scopes(actor,"app.read");
        var ids=await(from environment in db.Set<EnvironmentRecord>() join project in db.Set<Project>() on environment.ProjectId equals project.Id where project.OrganizationId==scope.OrganizationId&&(scope.ProjectId==null||project.Id==scope.ProjectId)&&authorized.Any(x=>x.EnvironmentId==environment.Id&&x.ProjectId==project.Id&&x.OrganizationId==project.OrganizationId) orderby environment.Id select environment.Id).ToArrayAsync(ct);
        // Serialize against publication's environment writes while the protected command holds its lock.
        var environments=await db.Set<EnvironmentRecord>().FromSqlInterpolated($"SELECT * FROM environments WHERE id=ANY({ids}) ORDER BY id FOR UPDATE").AsNoTracking().ToArrayAsync(ct);
        // Environment first, matching registration/publication lock order; ingestion locks node before credential.
        var keys=await db.Set<ApplicationCredential>().FromSqlInterpolated($"SELECT * FROM application_credentials WHERE application_id={app.Id} ORDER BY id FOR UPDATE").AsNoTracking().ToArrayAsync(ct);
        var nodes=await db.Set<GatewayNode>().AsNoTracking().Where(x=>ids.Contains(x.EnvironmentId)).OrderBy(x=>x.Id).ToArrayAsync(ct);
        var recent=now.AddHours(-24);var oldestHour=new DateTimeOffset(recent.Year,recent.Month,recent.Day,recent.Hour,0,0,TimeSpan.Zero);
        var lastUse=await db.Set<CredentialUsageHourly>().AsNoTracking().Where(x=>x.CredentialId==credentialId&&x.ApplicationId==app.Id&&ids.Contains(x.EnvironmentId)&&x.HourStart>=oldestHour&&x.LastUsedAt>=recent).Select(x=>(DateTimeOffset?)x.LastUsedAt).MaxAsync(ct);
        var narrowed=target.Status=="Active"&&(request.Status!="Active"||request.ValidFrom>target.ValidFrom||request.ExpiresAt<target.ExpiresAt);
        var working=keys.Where(x=>x.Id!=target.Id&&x.Status=="Active").Select(x=>new Window(x.ValidFrom,x.ExpiresAt)).ToList();
        if(request.Status=="Active")working.Add(new(request.ValidFrom,request.ExpiresAt));
        var workingGap=narrowed&&Gap(new(target.ValidFrom,target.ExpiresAt),working,now);
        var lastWorking=target.Status=="Active"&&target.ValidFrom<=now&&now<target.ExpiresAt&&!keys.Any(x=>x.Id!=target.Id&&x.Status=="Active"&&x.ValidFrom<=now&&now<x.ExpiresAt);
        var atRisk=new HashSet<Guid>();var publishedUnknown=ids.Length==0;var sources=new List<object>();
        foreach(var environment in environments)
        {
            var cohort=nodes.Where(x=>x.EnvironmentId==environment.Id).ToArray();var versions=new SortedSet<long>();if(environment.DesiredConfigVersion is >0)versions.Add(environment.DesiredConfigVersion.Value);
            foreach(var node in cohort)
            {
                if(node.CurrentConfigVersion>0)versions.Add(node.CurrentConfigVersion);
                var receipt=ReceiptVersion(node);if(receipt>0)versions.Add(receipt);
            }
            if(cohort.Length<2&&versions.Count>0||cohort.Any(x=>x.Enabled&&x.CurrentConfigVersion==0&&ReceiptVersion(x)==0))publishedUnknown=true;
            foreach(var version in versions)
            {
                try
                {
                    var artifact=await history.ReadAsync(environment.Id,version,ct);sources.Add(new{environmentId=environment.Id,version,artifact.Hash});var published=artifact.Snapshot.Applications.SingleOrDefault(x=>x.Id==app.Id);
                    var old=published?.Credentials.SingleOrDefault(x=>x.Id==credentialId);if(old is null||old.Status!="Active"||!narrowed)continue;
                    var replacements=published!.Credentials.Where(x=>x.Id!=credentialId&&x.Status=="Active").Select(x=>new Window(x.ValidFrom,x.ExpiresAt)).ToList();if(request.Status=="Active")replacements.Add(new(request.ValidFrom,request.ExpiresAt));
                    if(Gap(new(old.ValidFrom,old.ExpiresAt),replacements,now))atRisk.Add(environment.Id);
                }
                catch(ApiException error) when(error.Status is 404 or 422){publishedUnknown=true;sources.Add(new{environmentId=environment.Id,version,proof="Unknown"});}
            }
        }
        // No AK, SK, secret hash, global LastUsed or continuously changing wall-clock timestamp.
        var hash=Convert.ToHexStringLower(SHA256.HashData(CanonicalJson.Serialize(new{
            credentialId,applicationId=app.Id,applicationRevision=app.Revision,request,
            credentials=keys.OrderBy(x=>x.Id).Select(x=>new{x.Id,x.Revision,x.Status,x.ValidFrom,x.ExpiresAt,phase=Phase(x.ValidFrom,x.ExpiresAt,now)}),
            requestPhase=Phase(request.ValidFrom,request.ExpiresAt,now),grants,
            environments=environments.Select(x=>new{x.Id,x.Revision,x.DesiredConfigVersion,x.DeploymentSequence}),
            nodes=nodes.Select(x=>new{x.Id,x.InstanceId,x.Enabled,x.CurrentConfigVersion,x.CurrentDeploymentSequence,receiptVersion=ReceiptVersion(x)}),
            sources,lastWorking,workingGap,publishedUnknown,atRisk=atRisk.Order().ToArray(),lastUse
        })));
        return new(new(hash,target.Revision,app.Revision,lastWorking,atRisk.Order().ToArray(),publishedUnknown),narrowed&&(workingGap||atRisk.Count>0||publishedUnknown||lastUse is not null));
    }
    private static string Phase(DateTimeOffset from,DateTimeOffset to,DateTimeOffset now)=>now<from?"Future":now>=to?"Expired":"Current";
    private static bool Gap(Window original,IEnumerable<Window> alternatives,DateTimeOffset now)
    {
        var cursor=original.From>now?original.From:now;if(cursor>=original.To)return false;
        foreach(var candidate in alternatives.OrderBy(x=>x.From).ThenBy(x=>x.To)){if(candidate.To<=cursor)continue;if(candidate.From>cursor)return true;cursor=candidate.To;if(cursor>=original.To)return false;}
        return cursor<original.To;
    }
    private static long ReceiptVersion(GatewayNode node)
    {
        try
        {
            using var json=JsonDocument.Parse(node.Metadata??"{}");if(!json.RootElement.TryGetProperty("runtimeApplication",out var receipt)||!receipt.TryGetProperty("instanceId",out var instance)||instance.GetString()!=node.InstanceId||!receipt.TryGetProperty("configVersion",out var version)||!version.TryGetInt64(out var number)||number<1)return 0;
            return number;
        }
        catch(Exception error) when(error is JsonException or InvalidOperationException){return 0;}
    }
}
