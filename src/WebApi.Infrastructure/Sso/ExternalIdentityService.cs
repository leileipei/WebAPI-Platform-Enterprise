using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Governance;
using WebApi.Contracts.Security;
using WebApi.Contracts.Sso;
using WebApi.Infrastructure.Commands;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Sso;
public sealed class ExternalIdentityService(WebApiDbContext db,AuthorizationService auth,AuditedCommandExecutor commands,IdempotentCommandExecutor idempotency,TimeProvider clock)
{
    private static readonly ScopeRef Platform=new(Guid.Empty);
    private async Task Require(ActorContext actor,CancellationToken ct)
    {
        var resource=new ResourceRef("external_identity",Guid.Empty,Platform);
        await auth.RequireAsync(actor,"user.manage",resource,ct);await auth.RequireAsync(actor,"system.sso.manage",resource,ct);
    }
    public Task<CommandResult<UserDto>> CreateAsync(CreateSsoUserRequest request,CommandRequestContext command,ActorContext actor,CancellationToken ct=default)=>
        commands.ExecuteAsync(actor,Platform,"user.sso.create",async(_,token)=>{
            await Require(actor,token);
            return await idempotency.ExecuteAsync(new(actor.UserId,Platform,"user.sso.create",command.IdempotencyKey),CanonicalJson.Serialize(request),async commandToken=>{
                SsoIdentityValidator.Validate(request);var provider=await Provider(request.ProviderId,commandToken);
                await Unique(provider,request.Subject,null,commandToken);
                if(await db.Set<UserRecord>().AnyAsync(x=>x.Username==request.Username,commandToken))throw new ApiException(409,"duplicate_resource","用户名已存在。");
                var user=new UserRecord{Username=request.Username,DisplayName=request.DisplayName,Email=request.Email,AuthSource="sso",PasswordHash=null,
                    SecurityStamp=Guid.NewGuid().ToString("N"),CreatedAt=clock.GetUtcNow(),UpdatedAt=clock.GetUtcNow()};
                var binding=new UserExternalIdentity{UserId=user.Id,ProviderId=provider.Id,Issuer=provider.Issuer,Subject=request.Subject,CreatedAt=clock.GetUtcNow(),UpdatedAt=clock.GetUtcNow()};
                db.AddRange(user,binding);
                return new CommandResult<UserDto>(new(user.Id,user.Username,user.DisplayName,user.Email,user.Status,user.AuthSource,user.Revision,[]),RevisionTag.Format(user.Revision));
            },token);
        },ct);
    public async Task<ExternalIdentityDto> GetAsync(Guid userId,ActorContext actor,CancellationToken ct=default)
    {
        await Require(actor,ct);return View(await db.Set<UserExternalIdentity>().AsNoTracking().SingleOrDefaultAsync(x=>x.UserId==userId,ct)??throw Missing());
    }
    public Task<CommandResult<ExternalIdentityDto>> SaveAsync(Guid userId,UpdateExternalIdentityRequest request,string? tag,CommandRequestContext command,ActorContext actor,CancellationToken ct=default)=>
        commands.ExecuteAsync(actor,Platform,"user.external_identity.update",async(_,token)=>{
            await Require(actor,token);
            return await idempotency.ExecuteAsync(new(actor.UserId,Platform,"user.external_identity.update:"+userId,command.IdempotencyKey),CanonicalJson.Serialize(new{userId,request,tag}),async commandToken=>{
                var user=await db.Set<UserRecord>().SingleOrDefaultAsync(x=>x.Id==userId,commandToken)??throw Missing();
                if(user.AuthSource!="sso"||user.PasswordHash is not null)throw new ApiException(409,"local_identity_protected","本地账号不能转换或绑定为 SSO 账号。");
                var binding=await db.Set<UserExternalIdentity>().SingleOrDefaultAsync(x=>x.UserId==userId,commandToken)??throw Missing();RevisionTag.Require(tag,binding.Revision);
                if(user.Status!="Disabled")throw new ApiException(409,"sso_user_disable_required","修正身份绑定前请先停用用户。");
                SsoIdentityValidator.ValidateSubject(request.Subject);var provider=await Provider(request.ProviderId,commandToken);
                await Unique(provider,request.Subject,binding.Id,commandToken);
                binding.ProviderId=provider.Id;binding.Issuer=provider.Issuer;binding.Subject=request.Subject;binding.Enabled=request.Enabled;binding.Revision++;binding.UpdatedAt=clock.GetUtcNow();
                user.SecurityStamp=Guid.NewGuid().ToString("N");user.Revision++;user.UpdatedAt=clock.GetUtcNow();
                return new CommandResult<ExternalIdentityDto>(View(binding),RevisionTag.Format(binding.Revision));
            },token);
        },ct);
    private async Task<SsoProvider> Provider(Guid id,CancellationToken ct)
    {
        var provider=await db.Set<SsoProvider>().SingleOrDefaultAsync(x=>x.Id==id,ct)??throw new ApiException(422,"sso_provider_unavailable","身份源不可用。");
        if(provider.OrganizationId.HasValue&&!await db.Set<Organization>().AnyAsync(x=>x.Id==provider.OrganizationId&&x.Status=="Active",ct))
            throw new ApiException(422,"sso_provider_unavailable","身份源不可用。");
        return provider;
    }
    private async Task Unique(SsoProvider provider,string subject,Guid? except,CancellationToken ct)
    {
        if(await db.Set<UserExternalIdentity>().AnyAsync(x=>x.ProviderId==provider.Id&&x.Issuer==provider.Issuer&&x.Subject==subject&&x.Id!=except,ct))
            throw new ApiException(409,"duplicate_external_identity","该外部身份已有绑定。");
    }
    private static ExternalIdentityDto View(UserExternalIdentity identity)=>new(identity.Id,identity.UserId,identity.ProviderId,identity.Issuer,identity.Subject,identity.Enabled,identity.Revision);
    private static ApiException Missing()=>new(404,"external_identity_not_found","用户或身份绑定不存在。");
}
