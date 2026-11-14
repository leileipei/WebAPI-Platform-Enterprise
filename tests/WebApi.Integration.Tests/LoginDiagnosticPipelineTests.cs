using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class LoginDiagnosticPipelineTests
{
    [Fact]
    public async Task RealConnectionFailureProducesCorrelatedSafe503Evidence()
    {
        var messages=new List<string>();
        await using var api=new ApiFixture(enableLoginDiagnostics:true,diagnosticOutput:messages.Add);
        await api.InitializeAsync(b=>b.Configuration["Redis:Connection"]="127.0.0.1:1,abortConnect=false");
        using var response=await api.LoginAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);
        Assert.Contains("login_protection_unavailable",await response.Content.ReadAsStringAsync());
        var message=Assert.Single(messages);
        using var document=JsonDocument.Parse(message["login-diagnostic=".Length..]);
        var root=document.RootElement;
        Assert.Equal("matched",root.GetProperty("correlation").GetString());
        Assert.Equal("complete",root.GetProperty("diagnosticStatus").GetString());
        Assert.Contains(root.GetProperty("events").EnumerateArray(),e=>e.GetProperty("phase").GetString()=="RateTotal" &&
            e.GetProperty("error").GetString() is "BudgetElapsed" or "RedisConnection" or "RedisTimeout");
        Assert.DoesNotContain(api.Password,message);
        Assert.DoesNotContain(api.User.Username,message);
        Assert.DoesNotContain("127.0.0.1:1",message);
        Assert.DoesNotContain(response.Headers.TryGetValues("Set-Cookie",out var cookies)?cookies:[],x=>x.StartsWith("WebApi.Session="));
    }

    [Fact]
    public async Task RealAuditConstraintFailureIsDistinguishedFromLoginProtection()
    {
        var messages=new List<string>();
        await using var api=new ApiFixture(enableLoginDiagnostics:true,diagnosticOutput:messages.Add);
        await api.InitializeAsync();
        await using(var db=api.Context())
            await db.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION reject_diagnostic_audit() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'CANARY-AUDIT-DATABASE' USING ERRCODE='23514'; END $$;
                CREATE TRIGGER reject_diagnostic_audit BEFORE INSERT ON audit_logs
                FOR EACH ROW EXECUTE FUNCTION reject_diagnostic_audit();
                """);
        using var response=await api.LoginAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);
        Assert.Contains("authentication_audit_unavailable",await response.Content.ReadAsStringAsync());
        var message=Assert.Single(messages);
        using var document=JsonDocument.Parse(message["login-diagnostic=".Length..]);
        var root=document.RootElement;
        Assert.Equal("matched",root.GetProperty("correlation").GetString());
        Assert.Equal("complete",root.GetProperty("diagnosticStatus").GetString());
        Assert.Contains(root.GetProperty("events").EnumerateArray(),e=>e.GetProperty("phase").GetString()=="AuditSave" &&
            e.GetProperty("error").GetString()=="DbCheck" && e.GetProperty("sqlState").GetString()=="23514");
        Assert.DoesNotContain("CANARY",message);
        Assert.DoesNotContain(api.Password,message);
        Assert.DoesNotContain(response.Headers.TryGetValues("Set-Cookie",out var cookies)?cookies:[],x=>x.StartsWith("WebApi.Session="));
    }
}
