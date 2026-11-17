using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class CredentialCreateRevocationConcurrencyTests
{
    [Theory]
    [InlineData("permission", true, false)]
    [InlineData("permission", false, false)]
    [InlineData("role", true, false)]
    [InlineData("role", false, false)]
    [InlineData("scope", true, false)]
    [InlineData("scope", false, false)]
    [InlineData("permission", true, true)]
    [InlineData("permission", false, true)]
    [InlineData("role", true, true)]
    [InlineData("role", false, true)]
    [InlineData("scope", true, true)]
    [InlineData("scope", false, true)]
    public async Task Creation_retry_observes_real_authority_revocation_in_both_lock_orders(
        string loss, bool revokeFirst, bool crossProcess)
    {
        await using var api = new ApiFixture();
        await api.InitializeAsync();
        await api.SeedConsumerAsync();
        using var login = await api.LoginAsync();
        login.EnsureSuccessStatusCode();
        var (administrator, administratorClient) = await api.NewReviewerAsync("RevocationOperator");
        Guid roleId;
        long roleRevision;
        await using (var setup = api.Context())
        {
            var subjectRole = await (from ur in setup.Set<UserRole>() join r in setup.Set<Role>() on ur.RoleId equals r.Id
                where ur.UserId == api.User.Id select r).SingleAsync();
            Assert.False(subjectRole.IsSystem);
            roleId = subjectRole.Id;
            roleRevision = subjectRole.Revision;
            var adminRole = new Role { Code = "PlatformAdmin", Name = "Test revocation operator", IsSystem = true };
            setup.Add(adminRole);
            setup.Add(new UserRole { UserId = administrator.Id, RoleId = adminRole.Id });
            foreach (var permission in await setup.Set<Permission>()
                .Where(p => p.Code == "role.manage" || p.Code == "scope.manage" || p.Code == "user.manage").ToArrayAsync())
                setup.Add(new RolePermission { RoleId = adminRole.Id, PermissionId = permission.Id });
            await setup.SaveChangesAsync();
        }
        var operatorBefore = await Identity(administratorClient);
        Assert.Equal(administrator.Id, operatorBefore.Id);
        Assert.NotEqual(api.User.Id, operatorBefore.Id);
        Assert.DoesNotContain("credential.manage", operatorBefore.Permissions);
        Assert.Contains("role.manage", operatorBefore.Permissions);
        Assert.Contains("scope.manage", operatorBefore.Permissions);
        Assert.Contains("user.manage", operatorBefore.Permissions);

        await using var child = crossProcess ? await ChildControlPlane.StartAsync(api) : null;
        var writer = child?.Client ?? api.Client;
        if (child is not null)
        {
            Assert.NotEqual(System.Environment.ProcessId, child.ProcessId);
            Assert.NotEqual(api.Client.BaseAddress, writer.BaseAddress);
        }
        Assert.Equal(api.User.Id, (await Identity(writer)).Id);
        var credentialPath = $"/api/v1/applications/{api.Application.Id}/credentials";
        var window = new { validFrom = "2026-11-18T00:00:00Z", expiresAt = "2026-12-18T00:00:00Z" };
        var key = Guid.NewGuid().ToString("N");
        using var first = await ApiFixture.CommandAsync(writer, credentialPath, window, key);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        var credentialId = firstBody.GetProperty("credential").GetProperty("id").GetGuid();
        var secret = firstBody.GetProperty("secret").GetString()!;
        var apiKey = firstBody.GetProperty("apiKey").GetString()!;
        Assert.False(string.IsNullOrEmpty(secret));
        Assert.False(string.IsNullOrEmpty(apiKey));
        byte[] receiptBefore;
        string secretHash;
        long[] auditIdsBefore;
        await using (var before = api.Context())
        {
            receiptBefore = (await before.Set<IdempotencyRecord>().SingleAsync()).ResponseBytes!.ToArray();
            secretHash = (await before.Set<ApplicationCredential>().SingleAsync()).SecretHash;
            auditIdsBefore = await before.Set<AuditLog>().Select(x => x.Id).ToArrayAsync();
        }

        // Fetch CSRF before taking the test lock. Both competing calls below are real commands.
        var writerCsrf = await writer.GetFromJsonAsync<JsonElement>("/api/v1/auth/csrf");
        var adminCsrf = await administratorClient.GetFromJsonAsync<JsonElement>("/api/v1/auth/csrf");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task<HttpResponseMessage> Retry(string idempotencyKey) => Send(writer, HttpMethod.Post, credentialPath,
            window, writerCsrf.GetProperty("token").GetString()!, idempotencyKey, null, deadline.Token);
        var revokePath = loss switch
        {
            "permission" => $"/api/v1/roles/{roleId}/permissions",
            "role" => $"/api/v1/users/{api.User.Id}/roles",
            _ => $"/api/v1/users/{api.User.Id}/scopes"
        };
        object revokeBody = loss switch
        {
            "permission" => new { permissions = Array.Empty<string>() },
            "role" => new { roleIds = Array.Empty<Guid>() },
            _ => new { scopes = Array.Empty<ScopeGrantDto>() }
        };
        Task<HttpResponseMessage> Revoke() => Send(administratorClient, HttpMethod.Put, revokePath,
            revokeBody, adminCsrf.GetProperty("token").GetString()!, Guid.NewGuid().ToString("N"),
            RevisionTag.Format(loss == "permission" ? roleRevision : api.User.Revision), deadline.Token);

        var requests = new List<Task<HttpResponseMessage>>();
        var validationCompleted = false;
        try
        {
            Task<HttpResponseMessage> retry;
            Task<HttpResponseMessage> revoke;
            await using (var hold = api.Context())
            await using (var transaction = await hold.Database.BeginTransactionAsync(deadline.Token))
            {
                await hold.Database.ExecuteSqlRawAsync("SELECT pg_catalog.pg_advisory_xact_lock(8901202)", deadline.Token);
                var firstWaiting = revokeFirst ? Revoke() : Retry(key);
                requests.Add(firstWaiting);
                var firstPid = Assert.Single(await WaitForCommandWaiters(api, 1, deadline.Token));
                Assert.False(firstWaiting.IsCompleted);
                var secondWaiting = revokeFirst ? Retry(key) : Revoke();
                requests.Add(secondWaiting);
                var both = await WaitForCommandWaiters(api, 2, deadline.Token);
                Assert.Contains(firstPid, both);
                Assert.False(firstWaiting.IsCompleted);
                Assert.False(secondWaiting.IsCompleted);
                if (child is not null) await child.AssertWaitingAsync(api);
                retry = revokeFirst ? secondWaiting : firstWaiting;
                revoke = revokeFirst ? firstWaiting : secondWaiting;
                await transaction.CommitAsync(deadline.Token);
            }
            var revoked = await revoke;
            Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
            var retried = await retry;
            var retryText = await retried.Content.ReadAsStringAsync();
            var responseTexts = new List<string> { retryText };
            var denialCode = loss == "scope" ? "scope_denied" : "permission_denied";
            if (revokeFirst) await AssertDenied(retried, denialCode);
            else
            {
                Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
                Assert.True(retried.Headers.CacheControl?.NoStore);
                var replay = await retried.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal(credentialId, replay.GetProperty("credential").GetProperty("id").GetGuid());
                Assert.True(replay.GetProperty("secretUnavailable").GetBoolean());
                Assert.Equal(JsonValueKind.Null, replay.GetProperty("secret").ValueKind);
                Assert.Equal(JsonValueKind.Null, replay.GetProperty("apiKey").ValueKind);
            }
            foreach (var deniedKey in new[] { key, Guid.NewGuid().ToString("N") })
            {
                using var denied = await Retry(deniedKey);
                await AssertDenied(denied, denialCode);
                responseTexts.Add(await denied.Content.ReadAsStringAsync());
            }
            var identity = await Identity(writer);
            Assert.Equal(api.User.Id, identity.Id);
            Assert.Equal(loss == "scope", identity.Permissions.Contains("credential.manage"));
            if (loss == "scope") Assert.Empty(identity.Scopes);
            var operatorAfter = await Identity(administratorClient);
            Assert.Equal(operatorBefore.Permissions.Order(), operatorAfter.Permissions.Order());
            Assert.Equal(operatorBefore.Scopes.ToArray(), operatorAfter.Scopes.ToArray());

            await using var check = api.Context();
            var credential = Assert.Single(await check.Set<ApplicationCredential>().ToArrayAsync());
            Assert.Equal(credentialId, credential.Id);
            Assert.Equal(secretHash, credential.SecretHash);
            Assert.Equal("Active", credential.Status);
            Assert.Equal(1, credential.Revision);
            Assert.Equal(api.Application.Revision + 1, (await check.Set<ApplicationRecord>().SingleAsync()).Revision);
            var receipt = Assert.Single(await check.Set<IdempotencyRecord>().ToArrayAsync());
            Assert.Equal("credential.create", receipt.Operation);
            Assert.Equal(api.User.Id, receipt.ActorId);
            Assert.Equal(receiptBefore, receipt.ResponseBytes);
            var audit = await check.Set<AuditLog>().ToArrayAsync();
            Assert.Equal(api.User.Id, Assert.Single(audit, x => x.Action == "credential.create").UserId);
            var revokeAction = loss == "permission" ? "role.permissions" : loss == "role" ? "user.roles" : "user.scopes";
            Assert.Equal(administrator.Id, Assert.Single(audit, x => x.Action == revokeAction).UserId);
            // Login is audited too (including the independent process login). Only revocation may add a row.
            Assert.Equal(auditIdsBefore.Length + 1, audit.Length);
            Assert.All(auditIdsBefore, id => Assert.Contains(audit, x => x.Id == id));
            Assert.Equal(revokeAction, Assert.Single(audit, x => !auditIdsBefore.Contains(x.Id)).Action);
            foreach (var output in responseTexts.Concat([Encoding.UTF8.GetString(receipt.ResponseBytes!),
                string.Join('\n', audit.Select(x => x.BeforeJson + x.AfterJson))]))
            {
                Assert.DoesNotContain(secret, output);
                Assert.DoesNotContain(apiKey, output);
                Assert.DoesNotContain(secretHash, output);
                Assert.DoesNotContain("secretHash", output, StringComparison.OrdinalIgnoreCase);
            }
            validationCompleted = true;
        }
        finally
        {
            // The held transaction is disposed before cancellation/draining, including failed assertions.
            await deadline.CancelAsync();
            try { await Task.WhenAll(requests); }
            catch when (!validationCompleted) { /* Preserve the original failure after draining every request. */ }
            finally
            {
                foreach (var request in requests.Where(x => x.IsCompletedSuccessfully)) (await request).Dispose();
            }
        }
    }

    private static async Task<IdentityDto> Identity(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/auth/me");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<IdentityDto>())!;
    }

    private static async Task AssertDenied(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.False(body.TryGetProperty("credential", out _));
        Assert.False(body.TryGetProperty("secret", out _));
        Assert.False(body.TryGetProperty("apiKey", out _));
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path,
        object body, string csrf, string key, string? tag, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-Token", csrf);
        request.Headers.Add("Idempotency-Key", key);
        if (tag is not null) request.Headers.Add("If-Match", tag);
        return await client.SendAsync(request, ct);
    }

    private static async Task<HashSet<int>> WaitForCommandWaiters(ApiFixture api, int count, CancellationToken ct)
    {
        await using var connection = await api.Database.OpenAsync();
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(10))
        {
            await using var query = new NpgsqlCommand("""
                SELECT l.pid FROM pg_locks l JOIN pg_stat_activity a ON a.pid=l.pid
                WHERE l.locktype='advisory' AND l.classid=0 AND l.objid=8901202 AND l.objsubid=1
                  AND NOT l.granted AND a.datname=current_database()
                """, connection);
            var pids = new HashSet<int>();
            await using (var rows = await query.ExecuteReaderAsync(ct))
                while (await rows.ReadAsync(ct)) pids.Add(rows.GetInt32(0));
            if (pids.Count == count) return pids;
            await Task.Delay(10, ct); // Poll observed lock state; elapsed time never establishes ordering.
        }
        Assert.Fail($"Expected {count} real HTTP commands queued on the fixture governance lock.");
        return [];
    }
}
