using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WebApi.ConsoleHost;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;

// Test-only host. Never included in a product image. Uses a new test database and
// the actual control plane and console host. There are no HTTP fixture controls.
var owner = Environment.GetEnvironmentVariable("WEBAPI_PRODUCT_ACCEPTANCE_OWNER") ?? throw new InvalidOperationException("An explicit acceptance owner is required.");
if (!Guid.TryParse(owner, out _)) throw new InvalidOperationException("An explicit acceptance owner is required.");
var directory = Path.GetFullPath(args.Single());
if (!directory.EndsWith("product-counts-" + owner, StringComparison.Ordinal))
    throw new InvalidOperationException("A fresh owner-specific evidence directory is required.");
Directory.CreateDirectory(directory);
Directory.CreateDirectory(Path.Combine(directory, "commands"));
if (File.Exists(Path.Combine(directory, "context.json"))) throw new InvalidOperationException("Never overwrite an existing fixture.");
var revision = Environment.GetEnvironmentVariable("WEBAPI_PRODUCT_REVISION") ?? throw new InvalidOperationException("Fixed product revision required.");
if (revision.Length != 40 || revision.Any(c => !Uri.IsHexDigit(c))) throw new InvalidOperationException("Full product SHA required.");
var scenario = Environment.GetEnvironmentVariable("WEBAPI_PRODUCT_ACCEPTANCE_SCENARIO") ?? "project-counts";
if (scenario is not ("project-counts" or "permission-matrix")) throw new InvalidOperationException("Invalid acceptance scenario.");
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
await using var api = new ApiFixture();
await api.InitializeAsync();
if (scenario == "permission-matrix")
{
    await PermissionMatrixFixture.RunAsync(api, directory, owner, revision, Write);
    return;
}
await api.SeedConsumerAsync();
var accounts = new List<Account>();
var empty = new Project { OrganizationId = api.Organization.Id, Code = "A_EMPTY", Name = "空项目" };
var other = new Project { OrganizationId = api.Organization.Id, Code = "Z_OTHER", Name = "其他项目" };
var disabled = new Project { OrganizationId = api.Organization.Id, Code = "Z_DISABLED", Name = "停用项目", Status = "Disabled" };
string[] reads = ["organization.read", "project.read", "environment.read", "api.read", "app.read"];
await using (var db = api.Context())
{
    (await db.Set<Project>().SingleAsync()).Name = "多资源项目";
    db.AddRange(empty, other, disabled);
    db.Add(new EnvironmentRecord { ProjectId = api.Project.Id, Code = "DEV", Name = "停用开发环境", Status = "Disabled" });
    db.Add(new Api { OrganizationId = api.Organization.Id, ProjectId = api.Project.Id, Code = "RETIRED", Name = "已退役 API", LifecycleStatus = "Retired", OwnerUserId = api.User.Id });
    db.Add(new ApiVersion { ApiId = api.Api.Id, Version = "2.0", Status = "Draft", CreatedBy = api.User.Id });
    db.Add(new ApplicationRecord { OrganizationId = api.Organization.Id, ProjectId = api.Project.Id, Code = "DISABLED", Name = "停用应用", Status = "Disabled" });
    db.Add(new ApplicationRecord { OrganizationId = api.Organization.Id, Code = "SHARED", Name = "组织共享应用" });
    foreach (var project in new[] { other, disabled })
    {
        db.Add(new EnvironmentRecord { ProjectId = project.Id, Code = "TEST", Name = "测试环境" });
        db.Add(new Api { OrganizationId = api.Organization.Id, ProjectId = project.Id, Code = "ORDERS", Name = "订单 API", LifecycleStatus = "Draft", OwnerUserId = api.User.Id });
        db.Add(new ApplicationRecord { OrganizationId = api.Organization.Id, ProjectId = project.Id, Code = "CONSUMER_" + project.Code, Name = "消费应用" });
    }
    var permissions = await db.Set<Permission>().Where(p => reads.Contains(p.Code)).ToDictionaryAsync(p => p.Code);
    foreach (var name in new[] { "all", "no-environment", "no-api", "no-app", "no-count-reads", "project-only", "environment-only", "no-project-read", "no-governance-read", "revoke" })
    {
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var user = new UserRecord { Username = "counts-" + name, DisplayName = "计数验收 · " + name, SecurityStamp = Guid.NewGuid().ToString("N") };
        user.PasswordHash = new PasswordHasher<UserRecord>().HashPassword(user, password);
        var role = new Role { Code = "counts-" + name, Name = "计数验收 " + name, OrganizationId = api.Organization.Id };
        db.AddRange(user, role, new UserRole { UserId = user.Id, RoleId = role.Id });
        var excluded = name switch
        {
            "no-environment" => new[] { "environment.read" }, "no-api" => ["api.read"], "no-app" => ["app.read"],
            "no-count-reads" => ["environment.read", "api.read", "app.read"], "no-project-read" => ["project.read"], "no-governance-read" => ["project.read", "environment.read"], _ => Array.Empty<string>()
        };
        foreach (var code in reads.Except(excluded)) db.Add(new RolePermission { RoleId = role.Id, PermissionId = permissions[code].Id });
        var scoped = name is "project-only" or "environment-only" or "revoke";
        db.Add(new UserProjectScope { UserId = user.Id, OrganizationId = api.Organization.Id, ProjectId = scoped ? api.Project.Id : null,
            EnvironmentId = name == "environment-only" ? api.Environment.Id : null, AccessMode = "read" });
        if (name == "all")
        {
            db.Add(new UserProjectScope { UserId = user.Id, OrganizationId = api.Organization.Id, ProjectId = api.Project.Id, AccessMode = "read" });
            db.Add(new UserProjectScope { UserId = user.Id, OrganizationId = api.Organization.Id, ProjectId = api.Project.Id, EnvironmentId = api.Environment.Id, AccessMode = "read" });
        }
        accounts.Add(new Account(name, user.Username, password, user.Id, role.Id));
    }
    await db.SaveChangesAsync();
}
await using var console = ConsoleHostApp.Build(["--environment", "Development"], builder =>
{
    builder.WebHost.UseUrls("http://0.0.0.0:18080");
    builder.Configuration["Console:DistDirectory"] = "/console-dist";
    builder.Configuration["Console:ControlPlaneUrl"] = api.Client.BaseAddress!.ToString();
    builder.Logging.ClearProviders();
});
await console.StartAsync();
await Write("browser-private.json", new { owner, accounts });
await Write("context.json", new { owner, productRevision = revision, classification = "isolated-real-http-postgres-production-console",
    organizationId = api.Organization.Id, projectId = api.Project.Id, environmentId = api.Environment.Id,
    emptyProjectId = empty.Id, otherProjectId = other.Id, disabledProjectId = disabled.Id,
    accounts = accounts.Select(a => new { a.Name, a.Username, a.UserId }), retainedDatabase = true });
Console.WriteLine("Product count fixture ready; private credentials written without console output.");
try
{
    while (!File.Exists(Path.Combine(directory, "stop")))
    {
        foreach (var file in Directory.GetFiles(Path.Combine(directory, "commands"), "*.json").Order())
        {
            var commandId = Path.GetFileNameWithoutExtension(file);
            if (!Guid.TryParse(commandId, out _)) throw new InvalidOperationException("Command ID must be a UUID.");
            var receipt = "done-" + commandId + ".json";
            if (File.Exists(Path.Combine(directory, receipt))) continue;
            var command = JsonSerializer.Deserialize<RevokeCommand>(await File.ReadAllTextAsync(file), json)!;
            if (command.Action != "revoke" || !new[] { "environment.read", "api.read", "app.read" }.Contains(command.Permission))
                throw new InvalidOperationException("Only the fixed revocation scenarios are allowed.");
            var roleId = accounts.Single(a => a.Name == "revoke").RoleId;
            await using var db = api.Context();
            var permissionId = await db.Set<Permission>().Where(p => p.Code == command.Permission).Select(p => p.Id).SingleAsync();
            var changed = await db.Set<RolePermission>().Where(p => p.RoleId == roleId && p.PermissionId == permissionId).ExecuteDeleteAsync();
            if (changed != 1) throw new InvalidOperationException("Expected exactly one fixture grant to revoke.");
            await Write(receipt, new { commandId, command.Action, command.Permission, changed });
        }
        await Task.Delay(100);
    }
}
finally { await console.StopAsync(); }
await Write("stopped.json", new { owner, stopped = true, databaseRetained = true });

async Task Write(string name, object value)
{
    var file = Path.Combine(directory, name);
    var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
    if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    using var stream = new FileStream(file, options);
    await JsonSerializer.SerializeAsync(stream, value, json);
}
record Account(string Name, string Username, string Password, Guid UserId, Guid RoleId);
record RevokeCommand(string Action, string Permission);
