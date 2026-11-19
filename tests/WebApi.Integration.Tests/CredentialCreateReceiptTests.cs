using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class CredentialCreateReceiptTests
{
    private static object Window(int days = 30) => new
    {
        validFrom = DateTimeOffset.Parse("2026-11-17T00:00:00Z"),
        expiresAt = DateTimeOffset.Parse("2026-11-17T00:00:00Z").AddDays(days)
    };

    private static async Task Initialize(ApiFixture api)
    {
        await api.InitializeAsync();
        await api.SeedConsumerAsync();
        using var login = await api.LoginAsync();
        login.EnsureSuccessStatusCode();
    }

    private static async Task<HttpResponseMessage> Send(ApiFixture api, object body, string? key,
        string? csrf = null, Guid? applicationId = null)
    {
        csrf ??= await api.CsrfAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/applications/{applicationId ?? api.Application.Id}/credentials") { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-Token", csrf);
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return await api.Client.SendAsync(request);
    }

    private static async Task<JsonElement> Read(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Guid CredentialId(JsonElement value) => value.GetProperty("credential").GetProperty("id").GetGuid();

    private static void AssertReplay(JsonElement replay, Guid credentialId)
    {
        Assert.Equal(credentialId, CredentialId(replay));
        Assert.True(replay.GetProperty("secretUnavailable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, replay.GetProperty("secret").ValueKind);
        Assert.Equal(JsonValueKind.Null, replay.GetProperty("apiKey").ValueKind);
    }

    [Fact]
    public async Task TwentyConcurrentRequestsCommitOneCredentialAndOnlyOneSecretResponse()
    {
        await using var api = new ApiFixture();
        await Initialize(api);
        var key = Guid.NewGuid().ToString("N");
        var csrf = await api.CsrfAsync();
        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Send(api, Window(), key, csrf)));
        try
        {
            var bodies = await Task.WhenAll(responses.Select(Read));
            var id = Assert.Single(bodies.Select(CredentialId).Distinct());
            var first = Assert.Single(bodies, x => x.GetProperty("secret").ValueKind == JsonValueKind.String);
            Assert.False(first.GetProperty("secretUnavailable").GetBoolean());
            foreach (var replay in bodies.Where(x => x.GetProperty("secret").ValueKind == JsonValueKind.Null)) AssertReplay(replay, id);
            await using var db = api.Context();
            Assert.Equal(id, (await db.Set<ApplicationCredential>().SingleAsync()).Id);
            Assert.Single(await db.Set<IdempotencyRecord>().Where(x => x.Operation == "credential.create").ToArrayAsync());
            Assert.Equal(1, await db.Set<AuditLog>().CountAsync(x => x.Action == "credential.create"));
            Assert.Equal(api.Application.Revision + 1, (await db.Set<ApplicationRecord>().SingleAsync()).Revision);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task AReplacementCredentialCanBeCreatedForTheExactEnvironmentAndItsSecretIsReturnedOnce()
    {
        await using var api = new ApiFixture();
        await Initialize(api);
        var body = new
        {
            environmentId = api.Environment.Id,
            validFrom = DateTimeOffset.Parse("2026-11-17T00:00:00Z"),
            expiresAt = DateTimeOffset.Parse("2026-12-17T00:00:00Z")
        };
        using var created = await Send(api, body, "replacement-credential");
        var first = await Read(created);
        Assert.Equal(api.Environment.Id, first.GetProperty("credential").GetProperty("environmentId").GetGuid());
        Assert.Equal("EnvironmentBound", first.GetProperty("credential").GetProperty("bindingMode").GetString());
        Assert.Equal(JsonValueKind.String, first.GetProperty("secret").ValueKind);
        using var retried = await Send(api, body, "replacement-credential");
        AssertReplay(await Read(retried), CredentialId(first));
        await using var db = api.Context();
        var row = await db.Set<ApplicationCredential>().SingleAsync();
        Assert.Equal(api.Environment.Id, row.EnvironmentId);
        Assert.Equal(api.Project.Id, row.ProjectId);
        Assert.Equal("EnvironmentBound", row.BindingMode);
        Assert.Empty(await db.Set<FormalCredentialRotation>().ToArrayAsync());
    }

    [Fact]
    public async Task LostFirstResponseAndServerRestartReplayMetadataWithoutRecoveringSecret()
    {
        await using var api = new ApiFixture();
        await Initialize(api);
        var key = Guid.NewGuid().ToString("N");
        using (var first = await Send(api, Window(), key)) first.EnsureSuccessStatusCode(); // Deliberately do not consume the response body.
        Guid id;
        await using (var db = api.Context()) id = (await db.Set<ApplicationCredential>().SingleAsync()).Id;
        await api.RestartServerAsync();
        using var retry = await Send(api, Window(), key);
        AssertReplay(await Read(retry), id);
        await using var check = api.Context();
        Assert.Single(await check.Set<ApplicationCredential>().ToArrayAsync());
        Assert.Single(await check.Set<IdempotencyRecord>().ToArrayAsync());
        Assert.Equal(1, await check.Set<AuditLog>().CountAsync(x => x.Action == "credential.create"));
    }

    [Fact]
    public async Task ReceiptAuditGetAndListNeverContainSecretApiKeyOrSecretHash()
    {
        await using var api = new ApiFixture();
        await Initialize(api);
        using var firstResponse = await Send(api, Window(), "secret-boundary");
        var first = await Read(firstResponse);
        var id = CredentialId(first);
        var secret = first.GetProperty("secret").GetString()!;
        var apiKey = first.GetProperty("apiKey").GetString()!;
        await using var db = api.Context();
        var credential = await db.Set<ApplicationCredential>().SingleAsync();
        Assert.True(WebApi.Infrastructure.Security.ApiKeySecret.Verify(credential.SecretHash, secret));
        var receipt = Assert.Single(await db.Set<IdempotencyRecord>().ToArrayAsync());
        Assert.Equal(api.User.Id, receipt.ActorId);
        Assert.Equal($"{api.Organization.Id}:{api.Project.Id}:", receipt.ScopeKey);
        var receiptText = Encoding.UTF8.GetString(receipt.ResponseBytes!);
        using var saved = JsonDocument.Parse(receiptText);
        Assert.Equal(["credential"], saved.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(id, CredentialId(saved.RootElement));
        using var get = await api.Client.GetAsync($"/api/v1/credentials/{id}");
        using var list = await api.Client.GetAsync($"/api/v1/applications/{api.Application.Id}/credentials");
        get.EnsureSuccessStatusCode(); list.EnsureSuccessStatusCode();
        using var retry = await Send(api, Window(), "secret-boundary");
        var replay = await Read(retry);
        AssertReplay(replay, id);
        var outputs = new[] { receiptText, replay.GetRawText(), await get.Content.ReadAsStringAsync(), await list.Content.ReadAsStringAsync(),
            string.Join('\n', (await db.Set<AuditLog>().ToArrayAsync()).Select(x => x.BeforeJson + x.AfterJson)) };
        foreach (var output in outputs)
        {
            Assert.DoesNotContain(secret, output);
            Assert.DoesNotContain(apiKey, output);
            Assert.DoesNotContain(credential.SecretHash, output);
            Assert.DoesNotContain("secretHash", output, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task SameKeyDifferentWindowOrApplicationConflictsWithoutAnotherWrite()
    {
        await using var api = new ApiFixture();
        await Initialize(api);
        var other = new ApplicationRecord { OrganizationId = api.Organization.Id, ProjectId = api.Project.Id, Code = "OTHER", Name = "Other", Owner = "Owner" };
        await using (var setup = api.Context()) { setup.Add(other); await setup.SaveChangesAsync(); }
        using var first = await Send(api, Window(), "same-key");
        await Read(first);
        using var changed = await Send(api, Window(31), "same-key");
        using var foreign = await Send(api, Window(), "same-key", applicationId: other.Id);
        foreach (var response in new[] { changed, foreign })
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("idempotency_conflict", await response.Content.ReadAsStringAsync());
        }
        await using var db = api.Context();
        Assert.Single(await db.Set<ApplicationCredential>().ToArrayAsync());
        Assert.Single(await db.Set<IdempotencyRecord>().ToArrayAsync());
        Assert.Equal(other.Revision, (await db.Set<ApplicationRecord>().SingleAsync(x => x.Id == other.Id)).Revision);
    }

    [Fact]
    public async Task EquivalentUtcWindowsReplayTheSameCredential()
    {
        await using var api = new ApiFixture();
        await Initialize(api);
        using var first = await Send(api, Window(), "utc-window");
        var id = CredentialId(await Read(first));
        using var retry = await Send(api, new { validFrom = "2026-11-17T08:00:00+08:00", expiresAt = "2026-12-17T08:00:00+08:00" }, "utc-window");
        AssertReplay(await Read(retry), id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("contains space")]
    [InlineData("contains,comma")]
    public async Task MissingOrInvalidKeyIsRejectedBeforeCreatingAnything(string? key)
    {
        await using var api = new ApiFixture();
        await Initialize(api);
        using var response = await Send(api, Window(), key);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("idempotency_required", await response.Content.ReadAsStringAsync());
        await using var db = api.Context();
        Assert.Empty(await db.Set<ApplicationCredential>().ToArrayAsync());
        Assert.Empty(await db.Set<IdempotencyRecord>().ToArrayAsync());
        Assert.False(await db.Set<AuditLog>().AnyAsync(x => x.Action == "credential.create"));
    }

    [Theory]
    [InlineData("scope", HttpStatusCode.Forbidden)]
    [InlineData("readonly", HttpStatusCode.Forbidden)]
    [InlineData("role", HttpStatusCode.Forbidden)]
    [InlineData("permission", HttpStatusCode.Forbidden)]
    [InlineData("account", HttpStatusCode.Unauthorized)]
    [InlineData("application", HttpStatusCode.Conflict)]
    public async Task ReplayRechecksCurrentAuthorityAndApplicationState(string change, HttpStatusCode expected)
    {
        await using var api = new ApiFixture();
        await Initialize(api);
        var csrf = await api.CsrfAsync();
        using var first = await Send(api, Window(), "authority", csrf);
        await Read(first);
        await using (var db = api.Context())
        {
            switch (change)
            {
                case "scope": await db.Set<UserProjectScope>().ExecuteDeleteAsync(); break;
                case "readonly": await db.Set<UserProjectScope>().ExecuteUpdateAsync(s => s.SetProperty(x => x.AccessMode, "read")); break;
                case "role": await db.Set<UserRole>().Where(x => x.UserId == api.User.Id).ExecuteDeleteAsync(); break;
                case "permission":
                    var permission = await db.Set<Permission>().SingleAsync(x => x.Code == "credential.manage");
                    await db.Set<RolePermission>().Where(x => x.PermissionId == permission.Id).ExecuteDeleteAsync(); break;
                case "account": await db.Set<UserRecord>().Where(x => x.Id == api.User.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Disabled")); break;
                case "application": await db.Set<ApplicationRecord>().Where(x => x.Id == api.Application.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Disabled")); break;
            }
        }
        using var retry = await Send(api, Window(), "authority", csrf);
        Assert.Equal(expected, retry.StatusCode);
        await using var check = api.Context();
        Assert.Single(await check.Set<ApplicationCredential>().ToArrayAsync());
        Assert.Single(await check.Set<IdempotencyRecord>().ToArrayAsync());
        Assert.Equal(1, await check.Set<AuditLog>().CountAsync(x => x.Action == "credential.create"));
    }

    [Fact]
    public async Task SameKeyIsolatedByActorAndForeignProjectCannotReplay()
    {
        await using var api = new ApiFixture();
        await Initialize(api);
        using var first = await Send(api, Window(), "actor-boundary");
        var firstId = CredentialId(await Read(first));
        var (other, client) = await api.NewReviewerAsync("ProjectAdmin");
        using var second = await ApiFixture.CommandAsync(client, $"/api/v1/applications/{api.Application.Id}/credentials", Window(), "actor-boundary");
        var secondBody = await Read(second);
        Assert.NotEqual(firstId, CredentialId(secondBody));
        Assert.Equal(JsonValueKind.String, secondBody.GetProperty("secret").ValueKind);
        await using (var db = api.Context())
        {
            var otherProject = new Project { OrganizationId = api.Organization.Id, Code = "OTHER", Name = "Other" };
            db.Add(otherProject); await db.SaveChangesAsync();
            await db.Set<UserProjectScope>().Where(x => x.UserId == other.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ProjectId, otherProject.Id));
        }
        using var denied = await ApiFixture.CommandAsync(client, $"/api/v1/applications/{api.Application.Id}/credentials", Window(), "actor-boundary");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        await using var check = api.Context();
        Assert.Equal(2, await check.Set<ApplicationCredential>().CountAsync());
        Assert.Equal(2, await check.Set<IdempotencyRecord>().CountAsync());
        Assert.Equal(2, await check.Set<AuditLog>().CountAsync(x => x.Action == "credential.create"));
    }

    [Fact]
    public async Task ReceiptWriteFailureRollsBackCredentialRevisionAndAuditAndAllowsExplicitRetry()
    {
        await using var api = new ApiFixture();
        await Initialize(api);
        await using (var setup = api.Context())
            await setup.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION reject_test_credential_receipt() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'test receipt write failure'; END $$;
                CREATE TRIGGER reject_test_credential_receipt BEFORE INSERT ON idempotency_records
                FOR EACH ROW WHEN (NEW.operation = 'credential.create') EXECUTE FUNCTION reject_test_credential_receipt();
                """);
        using var failed = await Send(api, Window(), "atomic-receipt");
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        await using (var check = api.Context())
        {
            Assert.Empty(await check.Set<ApplicationCredential>().ToArrayAsync());
            Assert.Empty(await check.Set<IdempotencyRecord>().ToArrayAsync());
            Assert.False(await check.Set<AuditLog>().AnyAsync(x => x.Action == "credential.create"));
            Assert.Equal(api.Application.Revision, (await check.Set<ApplicationRecord>().SingleAsync()).Revision);
            await check.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_test_credential_receipt ON idempotency_records; DROP FUNCTION reject_test_credential_receipt();");
        }
        using var retry = await Send(api, Window(), "atomic-receipt");
        var created = await Read(retry);
        Assert.Equal(JsonValueKind.String, created.GetProperty("secret").ValueKind);
        using var replay = await Send(api, Window(), "atomic-receipt");
        AssertReplay(await Read(replay), CredentialId(created));
    }
}
