using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Commands;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;

namespace WebApi.Infrastructure.Delivery;

public sealed class VerificationReportStorageSettings(string directory) : IDisposable
{
    public string Directory { get; } = Path.IsPathFullyQualified(directory) ? Path.GetFullPath(directory) : throw new InvalidOperationException("Verification report storage must use an absolute directory.");
    public SemaphoreSlim UploadSlots { get; } = new(2, 2);
    public void Dispose() => UploadSlots.Dispose();
}
internal sealed record ReportOwner(ScopeRef Scope, Guid? ArtifactId, Guid? PromotionId);
public sealed class VerificationReportStore(WebApiDbContext db, ReleaseArtifactService artifacts, ScopeResolver scopes, AuthorizationService auth,
    AuditedCommandExecutor commands, IdempotentCommandExecutor idempotency, CommandRequestContext requestContext, VerificationReportStorageSettings settings, IServiceScopeFactory services)
{
    public const int MaximumBytes = 10 * 1024 * 1024;
    private static readonly UTF8Encoding strictUtf8 = new(false, true);
    private static readonly Regex activeHtml = new(@"<\s*(?:!doctype\s+html|html|script|svg|iframe|object|embed|body)(?:\s|>|/)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static ApiException Invalid() => new(422, "invalid_verification_report", "报告只支持非空 PDF 或严格 UTF-8 纯文本，不能包含可执行 HTML。");

    public async Task<VerificationReportDto> StoreAsync(Stream input, string contentType, Guid? artifactId, Guid? promotionId, ActorContext actor, CancellationToken ct)
    {
        var owner = await RequireOwnerAsync(artifactId, promotionId, actor, true, ct);
        if (contentType is not ("application/pdf" or "text/plain")) throw Invalid();
        if (!await settings.UploadSlots.WaitAsync(0, ct)) throw new ApiException(429, "report_upload_busy", "报告上传繁忙，请稍后重试。");
        string? temporary = null, final = null;
        VerificationReport? pending = null;
        var retained = false;
        try
        {
            using var content = new MemoryStream();
            var buffer = new byte[65536];
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                if (content.Length + read > MaximumBytes) throw new ApiException(413, "report_too_large", "报告不能超过 10 MiB。");
                await content.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            var bytes = content.ToArray();
            ValidateContent(bytes, contentType);
            var key = $"{owner.Scope.ProjectId:N}/{owner.Scope.EnvironmentId:N}/{Guid.NewGuid():N}.bin";
            final = Path.Combine(settings.Directory, key);
            var directory = Path.GetDirectoryName(final)!;
            EnsurePrivateDirectory(directory);
            temporary = final + ".tmp";
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await file.WriteAsync(bytes, ct); await file.FlushAsync(ct);
            }
            File.Move(temporary, final);
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            pending = new VerificationReport { OrganizationId = owner.Scope.OrganizationId, ProjectId = owner.Scope.ProjectId!.Value, EnvironmentId = owner.Scope.EnvironmentId!.Value, ArtifactId = artifactId, PromotionId = promotionId, StorageKey = key, ContentType = contentType, SizeBytes = bytes.Length, Sha256 = hash, CreatedBy = actor.UserId };
            var result = await commands.ExecuteAsync(actor, owner.Scope, "verification_report.upload", async (_, token) =>
            {
                var actual = await RequireOwnerAsync(artifactId, promotionId, actor, true, token);
                if (actual != owner) throw ScopeResolver.Missing();
                return await idempotency.ExecuteAsync(new(actor.UserId, actual.Scope, "verification_report.upload", requestContext.IdempotencyKey), CanonicalJson.Serialize(new { artifactId, promotionId, contentType, hash, size = bytes.Length }), inner =>
                { db.Add(pending); return Task.FromResult(View(pending)); }, token);
            }, ct);
            retained = result.Id == pending.Id;
            return result;
        }
        catch
        {
            // A missing commit receipt does not prove a rollback. Confirm on a new connection
            // before removing only this upload's file; keep it when persistence is uncertain.
            if (pending is not null)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await using var confirmation = services.CreateAsyncScope();
                    var independent = confirmation.ServiceProvider.GetRequiredService<WebApiDbContext>();
                    retained = await independent.Set<VerificationReport>().AsNoTracking().AnyAsync(r => r.Id == pending.Id && r.StorageKey == pending.StorageKey, timeout.Token);
                }
                catch { retained = db.Entry(pending).State == EntityState.Unchanged; }
            }
            throw;
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
            if (!retained && final is not null && File.Exists(final)) File.Delete(final);
            settings.UploadSlots.Release();
        }
    }

    public async Task<Stream> OpenAsync(Guid reportId, ActorContext actor, CancellationToken ct)
    {
        var report = await GetMetadataAsync(reportId, actor, ct);
        if (report.SizeBytes is < 1 or > MaximumBytes || !Regex.IsMatch(report.StorageKey, @"\A[0-9a-f]{32}/[0-9a-f]{32}/[0-9a-f]{32}\.bin\z", RegexOptions.CultureInvariant) || !report.StorageKey.StartsWith($"{report.ProjectId:N}/{report.EnvironmentId:N}/", StringComparison.Ordinal)) throw Corrupt();
        var path = Path.Combine(settings.Directory, report.StorageKey);
        EnsureNoLinks(path);
        FileStream stream;
        try { stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous); }
        catch (FileNotFoundException) { throw Corrupt(); }
        try
        {
            if (stream.Length != report.SizeBytes || Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)) != report.Sha256) throw Corrupt();
            stream.Position = 0; return stream;
        }
        catch { await stream.DisposeAsync(); throw; }
    }
    public async Task<VerificationReport> GetMetadataAsync(Guid id, ActorContext actor, CancellationToken ct)
    {
        var report = await db.Set<VerificationReport>().AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct) ?? throw ScopeResolver.Missing();
        var owner = await RequireOwnerAsync(report.ArtifactId, report.PromotionId, actor, false, ct);
        if (owner.Scope.OrganizationId != report.OrganizationId || owner.Scope.ProjectId != report.ProjectId || owner.Scope.EnvironmentId != report.EnvironmentId) throw ScopeResolver.Missing();
        return report;
    }
    internal async Task<ReportOwner> RequireOwnerAsync(Guid? artifactId, Guid? promotionId, ActorContext actor, bool write, CancellationToken ct)
    {
        if ((artifactId is null) == (promotionId is null)) throw new ApiException(422, "invalid_report_owner", "请关联一个制品或一个晋级申请。");
        ScopeRef scope;
        if (artifactId is Guid source)
        { var artifact = await artifacts.GetAsync(source, actor, ct); scope = await scopes.EnvironmentAsync(artifact.SourceEnvironmentId, ct); }
        else
        {
            var promotion = await db.Set<ReleasePromotion>().AsNoTracking().SingleOrDefaultAsync(p => p.Id == promotionId, ct) ?? throw ScopeResolver.Missing();
            await artifacts.GetAsync(promotion.ArtifactId, actor, ct);
            scope = await scopes.EnvironmentAsync(promotion.TargetEnvironmentId, ct);
            if (scope.OrganizationId != promotion.OrganizationId || scope.ProjectId != promotion.ProjectId || !await auth.CanAsync(actor, "environment.read", new("environment", promotion.TargetEnvironmentId, scope), ct) || !await auth.CanAsync(actor, "release.read", new("environment", promotion.TargetEnvironmentId, scope), ct)) throw ScopeResolver.Missing();
        }
        if (write) await auth.RequireAsync(actor, artifactId is not null ? "release.test.record" : "release.verify", new("environment", scope.EnvironmentId!.Value, scope), ct);
        return new(scope, artifactId, promotionId);
    }
    public static VerificationReportDto View(VerificationReport row) => new(row.Id, row.ArtifactId, row.PromotionId, row.ContentType, row.SizeBytes, row.Sha256, row.CreatedBy, row.CreatedAt);
    private static ApiException Corrupt() => new(422, "corrupt_verification_report", "报告文件完整性校验失败。");
    private static void ValidateContent(byte[] bytes, string type)
    {
        if (bytes.Length == 0) throw Invalid();
        if (type == "application/pdf")
        { if (bytes.Length < 8 || !bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8) || bytes[5] is < (byte)'1' or > (byte)'2' || bytes[6] != '.' || bytes[7] is < (byte)'0' or > (byte)'9') throw Invalid(); return; }
        string text;
        try { text = strictUtf8.GetString(bytes); } catch (DecoderFallbackException) { throw Invalid(); }
        try { if (text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')) || activeHtml.IsMatch(text)) throw Invalid(); }
        catch (RegexMatchTimeoutException) { throw Invalid(); }
    }
    private void EnsurePrivateDirectory(string path)
    {
        EnsureNoLinks(path);
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            var current = path;
            while (current.StartsWith(settings.Directory, StringComparison.Ordinal))
            { File.SetUnixFileMode(current, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); if (current == settings.Directory) break; current = Path.GetDirectoryName(current)!; }
        }
    }
    private void EnsureNoLinks(string path)
    {
        var current = path;
        while (current is not null)
        { if (new FileInfo(current).LinkTarget is not null || new DirectoryInfo(current).LinkTarget is not null) throw Corrupt(); current = Path.GetDirectoryName(current); }
    }
}
