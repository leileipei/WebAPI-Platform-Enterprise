using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WebApi.ConsoleHost;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;

internal static class PermissionMatrixFixture
{
    private const string Scenario = "permission-matrix";
    private const string Manage = "app.permission.manage";

    public static async Task RunAsync(ApiFixture api, string directory, string owner, string revision,
        Func<string, object, Task> write)
    {
        await api.SeedConsumerAsync(seedFreshEnvironment: true);
        var data = await SeedAsync(api);
        await using var console = ConsoleHostApp.Build(["--environment", "Development"], builder =>
        {
            builder.WebHost.UseUrls("http://0.0.0.0:18080");
            builder.Configuration["Console:DistDirectory"] = "/console-dist";
            builder.Configuration["Console:ControlPlaneUrl"] = api.Client.BaseAddress!.ToString();
            builder.Logging.ClearProviders();
        });
        await console.StartAsync();
        await write("browser-private.json", new { owner, scenario = Scenario, accounts = data.Accounts });
        await write("context.json", new
        {
            owner, scenario = Scenario, productRevision = revision,
            classification = "isolated-real-http-postgres-production-console",
            organizationId = api.Organization.Id, projectId = api.Project.Id,
            applicationId = api.Application.Id, sharedApplicationId = data.Shared.Id,
            environmentId = api.Environment.Id, prodId = data.Production.Id, inactiveId = data.Inactive.Id,
            apiId = api.Api.Id, timeWindowApiId = data.TimeWindows.Id, lastApiId = data.LastApi.Id,
            testRouteId = data.TestRoute.Id, productionRouteId = data.ProductionRoute.Id,
            secondProjectId = data.SecondProject.Id, secondEnvironmentId = data.SecondEnvironment.Id,
            secondApiId = data.SecondApi.Id, retainedDatabase = true,
            accounts = data.Accounts.Select(a => new { a.Name, a.Username, a.UserId })
        });
        Console.WriteLine("Permission matrix fixture ready; private credentials written without console output.");
        try
        {
            while (!File.Exists(Path.Combine(directory, "stop")))
            {
                await RunCommandsAsync(api, directory, owner, data, write);
                await Task.Delay(100);
            }
        }
        finally
        {
            await console.StopAsync();
        }
        await write("stopped.json", new { owner, scenario = Scenario, stopped = true, databaseRetained = true });
    }

    private static async Task<Seeded> SeedAsync(ApiFixture api)
    {
        await using var db = api.Context();
        var project = await db.Set<Project>().SingleAsync(p => p.Id == api.Project.Id);
        project.Name = "矩阵项目";
        var test = await db.Set<EnvironmentRecord>().SingleAsync(e => e.Id == api.Environment.Id);
        test.Name = "测试环境";
        var application = await db.Set<ApplicationRecord>().SingleAsync(a => a.Id == api.Application.Id);
        application.Name = "矩阵消费应用";

        var policy = new ApprovalFlow { OrganizationId = api.Organization.Id, Name = "合成审批模板", ScopeType = "environment" };
        var production = new EnvironmentRecord
        {
            ProjectId = project.Id, Code = "PROD", Name = "生产环境", IsProduction = true,
            SortOrder = 1, ReleasePolicyId = policy.Id
        };
        var inactive = new EnvironmentRecord { ProjectId = project.Id, Code = "OLD", Name = "停用环境", SortOrder = 2, Status = "Disabled" };
        var secondProject = new Project { OrganizationId = api.Organization.Id, Code = "SHARED_B", Name = "共享项目 B" };
        var secondEnvironment = new EnvironmentRecord { ProjectId = secondProject.Id, Code = "SIT", Name = "联调环境" };
        var shared = new ApplicationRecord { OrganizationId = api.Organization.Id, Code = "SHARED_MATRIX", Name = "共享矩阵应用", Owner = "合成负责人" };
        db.AddRange(policy, production, inactive, secondProject, secondEnvironment, shared);
        api.AddFreshEnvironmentOrigin(db, production);
        api.AddFreshEnvironmentOrigin(db, secondEnvironment);
        var productionCluster = new UpstreamCluster
        {
            ProjectId = project.Id, EnvironmentId = production.Id, Name = "合成生产集群", LoadBalancingPolicy = "RoundRobin",
            HealthCheckPath = "/health", HealthCheckIntervalSec = 30
        };
        db.Add(productionCluster);
        var timeWindows = NewApi(project.Id, "HISTORY", "历史授权");
        var lastApi = NewApi(project.Id, "ZZ_FINAL", "末页 API");
        var secondApi = NewApi(secondProject.Id, "SHARED_CATALOG", "联调 API");
        db.AddRange(timeWindows, lastApi, secondApi);
        db.AddRange(Enumerable.Range(1, 52).Select(n => NewApi(project.Id, $"ZZ_FILL_{n:D3}", $"分页用 API {n:D3}")));
        var lastVersion = new ApiVersion { ApiId = lastApi.Id, Version = "1.0.0", Status = "Draft", ChangeType = "compatible", CreatedBy = api.User.Id };
        db.Add(lastVersion);
        var testRoute = Route(api.Api.Id, api.Version.Id, test.Id, api.Cluster.Id, "/orders-test", ["GET", "POST"]);
        var productionRoute = Route(api.Api.Id, api.Version.Id, production.Id, productionCluster.Id, "/orders-production", ["POST"]);
        db.AddRange(testRoute, productionRoute, Route(lastApi.Id, lastVersion.Id, test.Id, api.Cluster.Id, "/last-test", ["GET"]));

        var now = DateTimeOffset.UtcNow;
        db.AddRange(new ApplicationApiPermission
        {
            ApplicationId = application.Id, ApiId = timeWindows.Id, EnvironmentId = test.Id,
            Mode = "LegacyApi", ValidFrom = now.AddDays(-2), ExpiresAt = now.AddDays(-1), CreatedBy = api.User.Id
        }, new ApplicationApiPermission
        {
            ApplicationId = application.Id, ApiId = timeWindows.Id, EnvironmentId = production.Id,
            Mode = "LegacyApi", ValidFrom = now.AddDays(1), ExpiresAt = now.AddDays(2), CreatedBy = api.User.Id
        });

        var managerRoleId = await db.Set<UserRole>().Where(r => r.UserId == api.User.Id).Select(r => r.RoleId).SingleAsync();
        var gatewayConfigRead = new Permission { Code = "gateway.config.read", Module = "gateway", Name = "读取配置" };
        db.AddRange(gatewayConfigRead, new RolePermission { RoleId = managerRoleId, PermissionId = gatewayConfigRead.Id });
        string[] codes = ["organization.read", "project.read", "environment.read", "api.read", "app.read", Manage, "route.read"];
        var permissions = await db.Set<Permission>().Where(p => codes.Contains(p.Code)).ToDictionaryAsync(p => p.Code);
        var accounts = new List<MatrixAccount> { new("manager", api.User.Username, api.Password, api.User.Id, managerRoleId) };
        foreach (var accountName in new[] { "viewer", "revoked", "no-environment" })
        {
            var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var user = new UserRecord { Username = "matrix-" + accountName, DisplayName = "矩阵验收 · " + accountName, SecurityStamp = Guid.NewGuid().ToString("N") };
            user.PasswordHash = new PasswordHasher<UserRecord>().HashPassword(user, password);
            var role = new Role { Code = "matrix-" + accountName, Name = "矩阵验收 " + accountName, OrganizationId = project.OrganizationId };
            db.AddRange(user, role, new UserRole { UserId = user.Id, RoleId = role.Id });
            foreach (var code in codes.Where(code => accountName != "no-environment" || code != "environment.read"))
                db.Add(new RolePermission { RoleId = role.Id, PermissionId = permissions[code].Id });
            db.Add(new UserProjectScope
            {
                UserId = user.Id, OrganizationId = project.OrganizationId, ProjectId = project.Id,
                AccessMode = accountName == "revoked" ? "read_write" : "read"
            });
            accounts.Add(new MatrixAccount(accountName, user.Username, password, user.Id, role.Id));
        }
        await db.SaveChangesAsync();
        return new(production, inactive, timeWindows, lastApi, testRoute, productionRoute, shared,
            secondProject, secondEnvironment, secondApi, accounts);

        Api NewApi(Guid projectId, string code, string name) => new()
        {
            OrganizationId = api.Organization.Id, ProjectId = projectId, Code = code, Name = name,
            LifecycleStatus = "Draft", OwnerUserId = api.User.Id
        };
    }

    private static ApiRoute Route(Guid apiId, Guid versionId, Guid environmentId, Guid clusterId, string path, string[] methods) => new()
    {
        ApiId = apiId, ApiVersionId = versionId, EnvironmentId = environmentId, ClusterId = clusterId,
        RouteName = path, Path = path, NormalizedPath = path, Methods = methods, Priority = 100, TimeoutMs = 30000
    };

    private static async Task RunCommandsAsync(ApiFixture api, string directory, string owner, Seeded data,
        Func<string, object, Task> write)
    {
        foreach (var file in Directory.GetFiles(Path.Combine(directory, "commands"), "*.json").Order())
        {
            var commandId = Path.GetFileNameWithoutExtension(file);
            if (!Guid.TryParse(commandId, out _)) throw new InvalidOperationException("Invalid fixture command ID.");
            var receipt = "done-" + commandId + ".json";
            if (File.Exists(Path.Combine(directory, receipt))) continue;
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(file));
            var request = json.RootElement;
            if (request.GetProperty("owner").GetString() != owner || request.GetProperty("action").GetString() != "revoke-matrix-management")
                throw new InvalidOperationException("The fixture accepts only its fixed permission-removal scenario.");
            await using var db = api.Context();
            var target = data.Accounts.Single(a => a.Name == "revoked");
            var permissionId = await db.Set<Permission>().Where(p => p.Code == Manage).Select(p => p.Id).SingleAsync();
            var removed = await db.Set<RolePermission>().Where(p => p.RoleId == target.RoleId && p.PermissionId == permissionId).ExecuteDeleteAsync();
            if (removed != 1) throw new InvalidOperationException("Expected to remove one synthetic role permission.");
            await write(receipt, new { owner, commandId, removed, target.UserId });
        }
    }

    private sealed record MatrixAccount(string Name, string Username, string Password, Guid UserId, Guid RoleId);

    private sealed record Seeded(EnvironmentRecord Production, EnvironmentRecord Inactive, Api TimeWindows, Api LastApi,
        ApiRoute TestRoute, ApiRoute ProductionRoute, ApplicationRecord Shared, Project SecondProject,
        EnvironmentRecord SecondEnvironment, Api SecondApi, IReadOnlyList<MatrixAccount> Accounts);
}
