using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Policies;
using WebApi.Gateway.Configuration;
using WebApi.Gateway.Storage;
using WebApi.Gateway.Tests.Support;
using WebApi.Gateway.Workers;
using WebApi.Infrastructure.Persistence.Entities;
using Xunit;
namespace WebApi.Gateway.Tests;
public sealed class CacheActivationTests
{
    [Theory] [InlineData("limit")] [InlineData("vary")] [InlineData("missing")]
    public async Task NodeDeploymentLimitRejectsActivationAndRetainsLkg(string reason)
    {
        GatewayFixture f = null!;
        f = new GatewayFixture { ConfigureControl = b => { if (reason == "vary") b.Configuration["GatewayPolicies:Cache:AllowedVaryHeaders:0"] = "X-Tenant"; }, ConfigureGateway = b =>
        {
            var file = Path.Combine(f.Directory, "cache-hmac.secret");
            if (!File.Exists(file)) { File.WriteAllText(file, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))); if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            b.Configuration["GatewayPolicies:Cache:HmacSecretFile"] = reason == "missing" ? Path.Combine(f.Directory, "missing.secret") : file;
            if (reason == "limit") b.Configuration["GatewayPolicies:Cache:MaxEntryBytes"] = "1024";
        } };
        await using (f)
        {
            await f.InitializeAsync(); var first = await f.PublishAsync(); await f.ApplyBothAsync(first);
            await using (var db = f.Control.Context())
            {
                var config = CacheEligibilityTests.Config with { MaxEntryBytes = 2048, VaryHeaders = reason == "vary" ? ["X-Tenant"] : [] };
                var policy = new Policy { OrganizationId = f.Control.Organization.Id, ProjectId = f.Control.Project.Id, Name = "共享缓存", Type = "cache", Config = JsonSerializer.Serialize(config, new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
                db.Add(policy); db.Add(new RoutePolicyBinding { RouteId = f.RouteId, PolicyId = policy.Id }); (await db.Set<ApiRoute>().SingleAsync()).Revision++; await db.SaveChangesAsync();
            }
            var second = await f.PublishAsync(); var result = await f.Gateways[0].Services.GetRequiredService<ConfigWatcher>().AcceptAsync(second);
            Assert.False(result.Applied); Assert.Equal(first.Envelope, f.Gateways[0].Services.GetRequiredService<RuntimeGenerationStore>().Current!.Envelope); Assert.Equal(first.Envelope, (await f.Gateways[0].Services.GetRequiredService<LkgStore>().ReadAsync())!.Envelope);
            using var response = await f.RequestAsync(); response.EnsureSuccessStatusCode();
        }
    }
}
