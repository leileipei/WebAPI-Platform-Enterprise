using WebApi.Infrastructure.Settings;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
namespace WebApi.ControlPlane.Security;
public static class SessionEndpoints
{
    public static ActorContext Actor(this HttpContext context) => new(Guid.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!),context.TraceIdentifier);
    private static ApiException AuditUnavailable(HttpContext ctx)
    {
        AuthenticationProtectionMetrics.AuditFailure();ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("AuthenticationProtection").LogWarning("Authentication audit unavailable; traceId={TraceId}",ctx.TraceIdentifier);
        return new(503,"authentication_audit_unavailable","登录保护暂不可用，请稍后重试。");
    }
    private static async Task WriteOutsideAuthentication(HttpContext ctx,AuthenticationAuditEvent entry,CancellationToken ct)
    {
        await using var scope=ctx.RequestServices.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        try{await scope.ServiceProvider.GetRequiredService<IAuthenticationAuditWriter>().WriteAsync(entry,ct);}
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch{throw AuditUnavailable(ctx);}
    }
    public static void MapSessions(this WebApplication app)
    {
        var group=app.MapGroup("/api/v1/auth").AddEndpointFilter<RequestValidationFilter>();
        group.MapGet("/csrf",(HttpContext ctx,IAntiforgery antiforgery)=> {ctx.Response.Headers.CacheControl="no-store";return Results.Ok(new { token=antiforgery.GetAndStoreTokens(ctx).RequestToken });}).AllowAnonymous();
        group.MapPost("/login",async (LoginRequest request,HttpContext ctx,AccountService accounts,WebApiDbContext db,SystemSettingsReader settings,Microsoft.AspNetCore.Identity.IPasswordHasher<UserRecord> hasher,PlatformSessionIssuer issuer,ILoginRateStore rates,LoginKeyHasher keys,IAuthenticationAuditGate auditGate,IAuthenticationAuditWriter audit,CancellationToken ct)=> {
            ctx.Response.Headers.CacheControl="no-store";
            if(string.IsNullOrWhiteSpace(request.Username)||request.Username.Length>128||string.IsNullOrEmpty(request.Password)||request.Password.Length>1024)
                throw new ApiException(401,"invalid_credentials","用户名或密码错误。");
            var security=await settings.SecurityAsync(ct);var identity=keys.Keys(ctx.Connection.RemoteIpAddress,request.Username);
            var decision=await rates.TryAcquireAsync(new(identity,new(security.LoginIpMaxAttempts,security.LoginIpWindowSeconds,security.LoginAccountMaxAttempts,security.LoginAccountWindowSeconds)),ct);
            if(decision.Kind==LoginRateDecisionKind.Unavailable)throw new ApiException(503,"login_protection_unavailable","登录保护暂不可用，请稍后重试。");
            if(decision.Kind==LoginRateDecisionKind.Limited)
            {
                AuthenticationProtectionMetrics.Reject();ctx.Response.Headers.RetryAfter=decision.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                AuthenticationAuditLease? lease=null;
                try
                {
                    lease=await auditGate.ClaimAsync(identity.Ip,ct);
                    if(lease is not null)
                    {
                        await WriteOutsideAuthentication(ctx,new("auth.login.throttled",null,ctx.Connection.RemoteIpAddress,ctx.TraceIdentifier,null,"login_rate_limited",lease),ct);
                        await auditGate.ConfirmAsync(lease,ct);
                    }
                }
                catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
                catch
                {
                    if(lease is not null)try{await auditGate.ReleaseAsync(lease,ct);}catch{ }
                    throw AuditUnavailable(ctx);
                }
                throw new ApiException(429,"login_rate_limited","登录尝试过于频繁，请稍后重试。");
            }
            UserRecord user;
            try
            {
                user=await accounts.AuthenticateAsync(request,ct);
                // Recheck status inside the write transaction; locks serialize concurrent user disablement.
                await using var tx=await db.Database.BeginTransactionAsync(ct);
                await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(8901202)",ct);
                user=await db.Set<UserRecord>().FromSqlInterpolated($"SELECT * FROM users WHERE id={user.Id} FOR UPDATE").AsNoTracking().SingleAsync(ct);
                if(user.Status!="Active"||user.AuthSource!="local"||user.PasswordHash is null||hasher.VerifyHashedPassword(user,user.PasswordHash,request.Password)==Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed)
                    throw new ApiException(401,"invalid_credentials","用户名或密码错误。");
                try{await audit.WriteAsync(new("auth.login",user.Id,ctx.Connection.RemoteIpAddress,ctx.TraceIdentifier,identity.AuditAccount,"authenticated"),ct);await tx.CommitAsync(ct);}
                catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
                catch{throw AuditUnavailable(ctx);}
            }
            catch(ApiException error) when(error.Status==401&&error.Code=="invalid_credentials")
            {
                await WriteOutsideAuthentication(ctx,new("auth.login.failed",null,ctx.Connection.RemoteIpAddress,ctx.TraceIdentifier,identity.AuditAccount,"invalid_credentials"),ct);throw;
            }
            await issuer.IssueAsync(ctx,user.Id,user.SecurityStamp,security.SessionTtlMinutes,null,ct);
            return Results.Ok(await accounts.IdentityAsync(user.Id,ct));
        }).AllowAnonymous();
        group.MapPost("/logout",async (HttpContext ctx,CancellationToken ct)=>{
            var user=ctx.Actor().UserId;await ctx.SignOutAsync();ctx.Response.Headers.CacheControl="no-store";
            try{await WriteOutsideAuthentication(ctx,new("auth.logout",user,ctx.Connection.RemoteIpAddress,ctx.TraceIdentifier,null,"logged_out"),ct);}
            catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
            catch{ } // Cookie already cleared; audit outage must not keep the user signed in.
            return Results.NoContent();
        }).RequireAuthorization();
        group.MapGet("/me",async(HttpContext ctx,AccountService accounts,CancellationToken ct)=>{ctx.Response.Headers.CacheControl="no-store";return Results.Ok(await accounts.IdentityAsync(ctx.Actor().UserId,ct));}).RequireAuthorization();
    }
}
