using WebApi.Infrastructure.Settings;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
namespace WebApi.ControlPlane.Security;
public static class SessionEndpoints
{
    public static ActorContext Actor(this HttpContext context) => new(Guid.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!),context.TraceIdentifier);
    public static void MapSessions(this WebApplication app)
    {
        var group=app.MapGroup("/api/v1/auth").AddEndpointFilter<RequestValidationFilter>();
        group.MapGet("/csrf",(HttpContext ctx,IAntiforgery antiforgery)=> {ctx.Response.Headers.CacheControl="no-store";return Results.Ok(new { token=antiforgery.GetAndStoreTokens(ctx).RequestToken });}).AllowAnonymous();
        group.MapPost("/login",async (LoginRequest request,HttpContext ctx,AccountService accounts,WebApiDbContext db,SystemSettingsReader settings,TimeProvider clock,Microsoft.AspNetCore.Identity.IPasswordHasher<UserRecord> hasher,PlatformSessionIssuer issuer,CancellationToken ct)=> {
            var user=await accounts.AuthenticateAsync(request,ct);
            // Recheck status inside the write transaction; locks serialize concurrent user disablement.
            await using var tx=await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(8901202)",ct);
            user=await db.Set<UserRecord>().FromSqlInterpolated($"SELECT * FROM users WHERE id={user.Id} FOR UPDATE").AsNoTracking().SingleAsync(ct);
            if(user.Status!="Active" || user.AuthSource!="local" || user.PasswordHash is null || hasher.VerifyHashedPassword(user,user.PasswordHash,request.Password)==Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed)
                throw new WebApi.Contracts.Common.ApiException(401,"invalid_credentials","用户名或密码错误。");
            var ttl=(await settings.SecurityAsync(ct)).SessionTtlMinutes;
            db.Add(new AuditLog {UserId=user.Id,Action="auth.login",ResourceType="user",ResourceId=user.Id.ToString(),Ip=ctx.Connection.RemoteIpAddress,TraceId=ctx.TraceIdentifier});
            await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
            await issuer.IssueAsync(ctx,user.Id,user.SecurityStamp,ttl,null,ct);
            ctx.Response.Headers.CacheControl="no-store";
            return Results.Ok(await accounts.IdentityAsync(user.Id,ct));
        }).AllowAnonymous();
        group.MapPost("/logout",async (HttpContext ctx)=>{await ctx.SignOutAsync();return Results.NoContent();}).RequireAuthorization();
        group.MapGet("/me",async(HttpContext ctx,AccountService accounts,CancellationToken ct)=>{ctx.Response.Headers.CacheControl="no-store";return Results.Ok(await accounts.IdentityAsync(ctx.Actor().UserId,ct));}).RequireAuthorization();
    }
}
