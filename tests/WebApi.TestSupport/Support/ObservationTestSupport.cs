using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using WebApi.Infrastructure.Persistence.Entities;

namespace WebApi.Integration.Tests.Support;

public static class ObservationTestSupport
{
    private static readonly HashSet<string> Allowed = ["metrics.read", "log.read", "trace.read", "alert.read", "alert.operate", "alert.rule.manage"];
    public static async Task GrantAsync(ApiFixture fixture, string[] permissions, string mode = "read_write", CancellationToken ct = default)
    {
        if (permissions.Any(p => !Allowed.Contains(p)) || mode is not ("read_write" or "read"))
            throw new ArgumentException("Only observability permissions in the disposable fixture are supported.");
        await using var db = fixture.Context();
        var roleId = await db.Set<UserRole>().Where(x => x.UserId == fixture.User.Id).Select(x => x.RoleId).FirstAsync(ct);
        foreach (var code in permissions.Distinct())
        {
            var permission = await db.Set<Permission>().SingleOrDefaultAsync(x => x.Code == code, ct);
            if (permission is null) { permission = new() { Code = code, Name = code, Module = code.Split('.')[0] }; db.Add(permission); }
            if (!await db.Set<RolePermission>().AnyAsync(x => x.RoleId == roleId && x.PermissionId == permission.Id, ct))
                db.Add(new RolePermission { RoleId = roleId, PermissionId = permission.Id });
        }
        var grant = await db.Set<UserProjectScope>().SingleAsync(x => x.UserId == fixture.User.Id && x.OrganizationId == fixture.Organization.Id && x.ProjectId == null && x.EnvironmentId == null, ct);
        grant.AccessMode = mode;
        await db.SaveChangesAsync(ct);
    }
}

public sealed record RecordedSourceRequest(HttpMethod Method, Uri Uri, string? Body);
public sealed class RecordingSourceHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<(string Json, int Status)> responses = new();
    public ConcurrentQueue<RecordedSourceRequest> Requests { get; } = new();
    public void Enqueue(string json, int status = 200) => responses.Enqueue((json, status));
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Enqueue(new(request.Method, request.RequestUri!, request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
        if (!responses.TryDequeue(out var response)) throw new InvalidOperationException("No source protocol response was configured.");
        return new((HttpStatusCode)response.Status) { Content = new StringContent(response.Json, Encoding.UTF8, "application/json") };
    }
}
