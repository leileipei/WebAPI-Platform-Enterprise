using System.Net;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Security;
public sealed record AuthenticationAuditEvent(string Action,Guid? UserId,IPAddress? Ip,string TraceId,string? AccountFingerprint,string Code,AuthenticationAuditLease? Lease=null);
public interface IAuthenticationAuditWriter { Task WriteAsync(AuthenticationAuditEvent entry,CancellationToken ct); }
public static class AuthenticationProtectionMetrics
{
    private static readonly Meter Meter=new("WebApi.AuthenticationProtection");
    private static readonly Counter<long> Throttled=Meter.CreateCounter<long>("authentication.login.throttled");
    private static readonly Counter<long> AuditFailures=Meter.CreateCounter<long>("authentication.audit.failures");
    public static void Reject()=>Throttled.Add(1);
    public static void AuditFailure()=>AuditFailures.Add(1);
}
public sealed class AuthenticationAuditWriter(WebApiDbContext db) : IAuthenticationAuditWriter
{
    private sealed record Summary(string Code,string? AccountFingerprint,DateTimeOffset? FirstRejectedAt);
    public async Task WriteAsync(AuthenticationAuditEvent entry,CancellationToken ct)
    {
        var expected=entry.Action switch{"auth.login"=>"authenticated","auth.login.failed"=>"invalid_credentials","auth.login.throttled"=>"login_rate_limited","auth.logout"=>"logged_out",_=>throw new ArgumentException("Invalid authentication audit action.")};
        var anonymous=entry.Action is "auth.login.failed" or "auth.login.throttled";
        if(entry.Code!=expected||anonymous&&entry.UserId is not null||!anonymous&&entry.UserId is null||entry.TraceId.Length>128||entry.TraceId.Any(char.IsControl)||entry.AccountFingerprint is { } fingerprint&&(fingerprint.Length!=64||fingerprint.Any(c=>c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))||entry.Action=="auth.login.throttled"&&entry.Lease is null)
            throw new ArgumentException("Invalid authentication audit summary.");
        var resource=entry.Lease is null?entry.UserId?.ToString()??"local-login":Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Lease.BucketKey)));
        await using var ownTransaction=db.Database.CurrentTransaction is null?await db.Database.BeginTransactionAsync(ct):null;
        if(entry.Lease is not null)
        {
            // Redis lease alone cannot prevent duplicate DB commits after caller crashes before confirmation.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({resource},0))",ct);
            if(await db.Set<AuditLog>().AnyAsync(x=>x.Action=="auth.login.throttled"&&x.ResourceId==resource,ct))
            {if(ownTransaction is not null)await ownTransaction.CommitAsync(ct);return;}
        }
        db.Add(new AuditLog{UserId=entry.UserId,Action=entry.Action,ResourceType="authentication",ResourceId=resource,Ip=entry.Ip,TraceId=entry.TraceId,AfterJson=JsonSerializer.Serialize(new Summary(entry.Code,entry.AccountFingerprint,entry.Lease?.FirstRejectedAt),CanonicalJson.Options)});
        await db.SaveChangesAsync(ct);if(ownTransaction is not null)await ownTransaction.CommitAsync(ct);
    }
}
