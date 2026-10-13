using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Infrastructure.Policies;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class TransformPreviewTests
{
    private const string Endpoint = "/api/v1/policies/transform/preview";
    public const string Config = """{"requestHeaders":[],"responseHeaders":[],"path":null,"query":[],"requestBody":{"methods":["POST"],"maxBodyBytes":1024,"operations":[{"op":"add","path":"/nullable","value":null}]},"responseBody":{"statusCodes":[200],"maxBodyBytes":1024,"operations":[{"op":"move","from":"/data","path":"/result"}]}}""";

    internal static async Task<ApiFixture> PrepareAsync(bool readOnly = false, int maxBytes = 1048576, ILoggerProvider? logging = null, int maxConcurrent = 16)
    {
        var api = new ApiFixture(); await api.InitializeAsync(builder => { builder.Configuration["GatewayPolicies:Transform:MaxBodyBytes"] = maxBytes.ToString(); builder.Configuration["GatewayPolicies:Transform:MaxConcurrentBodyTransforms"] = maxConcurrent.ToString(); if (logging is not null) builder.Logging.AddProvider(logging); });
        await api.SeedScopeAsync(readOnly ? "read_only" : "read_write");
        await using var db = api.Context(); var role = await db.Set<UserRole>().Where(r => r.UserId == api.User.Id).Select(r => r.RoleId).SingleAsync();
        foreach (var code in readOnly ? new[] { "policy.read" } : new[] { "policy.read", "policy.write" })
        { var permission = new Permission { Code=code, Name=code, Module="policy" }; db.Add(permission); db.Add(new RolePermission {RoleId=role,PermissionId=permission.Id}); }
        await db.SaveChangesAsync(); using var login = await api.LoginAsync(); login.EnsureSuccessStatusCode(); return api;
    }
    private static object Request(ApiFixture api, string sample = "{\"big\":9007199254740993}", string direction = "Request", string config = Config) =>
        new {scope=new {organizationId=api.Organization.Id,projectId=(Guid?)null},config,direction,sampleJson=sample};

    [Fact]
    public async Task ReadOnlyPreview_IsPreciseNoStoreAndDoesNotWritePolicyOrAudit()
    {
        await using var api = await PrepareAsync(readOnly:true);
        await using var db = api.Context(); var before = await db.Set<AuditLog>().CountAsync();
        using var response = await api.WriteAsync(HttpMethod.Post, Endpoint, Request(api)); response.EnsureSuccessStatusCode();
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("JsonMappingPreview", result.RootElement.GetProperty("kind").GetString()); Assert.False(result.RootElement.GetProperty("actualForwarding").GetBoolean());
        Assert.Equal(1, result.RootElement.GetProperty("operationCount").GetInt32());
        var output = result.RootElement.GetProperty("outputJson"); Assert.Equal(JsonValueKind.String, output.ValueKind);
        using var doc = JsonDocument.Parse(output.GetString()!); Assert.Equal("9007199254740993", doc.RootElement.GetProperty("big").GetRawText());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("nullable").ValueKind);
        Assert.False(await db.Set<Policy>().AnyAsync()); Assert.Equal(before, await db.Set<AuditLog>().CountAsync());
    }

    [Fact]
    public async Task ResponsePreview_UsesSameEngineWithoutForwarding()
    {
        await using var api = await PrepareAsync(); using var response = await api.WriteAsync(HttpMethod.Post,Endpoint,Request(api,"{\"data\":{\"stock\":10}}","Response")); response.EnsureSuccessStatusCode();
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        using var output = JsonDocument.Parse(result.RootElement.GetProperty("outputJson").GetString()!);
        Assert.Equal(10,output.RootElement.GetProperty("result").GetProperty("stock").GetInt32()); Assert.False(result.RootElement.GetProperty("actualForwarding").GetBoolean());
    }

    [Fact]
    public async Task Preview_RequiresCsrfAndReadPermissionAndHidesForeignScope()
    {
        await using var api = await PrepareAsync();
        using var csrf = await api.Client.PostAsJsonAsync(Endpoint,Request(api)); Assert.Equal(HttpStatusCode.Forbidden,csrf.StatusCode);
        Guid foreign;
        await using (var db = api.Context()) { var org=new Organization {Code="FOREIGN",Name="Foreign"};db.Add(org);await db.SaveChangesAsync();foreign=org.Id; }
        using var denied = await api.WriteAsync(HttpMethod.Post,Endpoint,new {scope=new {organizationId=foreign},config="invalid",direction="Request",sampleJson="private"}); Assert.Equal(HttpStatusCode.NotFound,denied.StatusCode);
        await using (var db = api.Context()) {var permission=await db.Set<Permission>().SingleAsync(p=>p.Code=="policy.read");await db.Set<RolePermission>().Where(p=>p.PermissionId==permission.Id).ExecuteDeleteAsync();}
        using var noRead = await api.WriteAsync(HttpMethod.Post,Endpoint,Request(api)); Assert.Contains(noRead.StatusCode,new[] {HttpStatusCode.NotFound,HttpStatusCode.Forbidden});
    }

    [Theory]
    [InlineData("Unknown", "{}", 422, "invalid_request")]
    [InlineData("Request", "{\"x\":1,\"x\":2}", 400, "transform_rejected")]
    [InlineData("Response", "{}", 422, "transform_rejected")]
    public async Task Preview_ErrorsAreControlled(string direction,string sample,int status,string code)
    {
        await using var api=await PrepareAsync();using var response=await api.WriteAsync(HttpMethod.Post,Endpoint,Request(api,sample,direction)); Assert.Equal(status,(int)response.StatusCode);
        using var result=JsonDocument.Parse(await response.Content.ReadAsStringAsync()); Assert.Equal(code,result.RootElement.GetProperty("code").GetString());
        if(direction=="Response") {Assert.Equal("transform_json_operation_failed",result.RootElement.GetProperty("reason").GetString());Assert.Equal(1,result.RootElement.GetProperty("operationIndex").GetInt32());}
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"scope\":null,\"config\":\"{}\",\"direction\":\"Request\",\"sampleJson\":\"{}\"}")]
    [InlineData("{\"scope\":{\"organizationId\":\"00000000-0000-0000-0000-000000000000\"},\"config\":\"{}\",\"direction\":\"Request\",\"sampleJson\":\"{}\"}")]
    public async Task Preview_MissingFieldsAndEmptyScope_Are422(string source)
    {
        await using var api=await PrepareAsync();using var request=new HttpRequestMessage(HttpMethod.Post,Endpoint) {Content=new StringContent(source,Encoding.UTF8,"application/json")}; request.Headers.Add("X-CSRF-Token",await api.CsrfAsync());using var response=await api.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,response.StatusCode);using var result=JsonDocument.Parse(await response.Content.ReadAsStringAsync());Assert.Equal("invalid_request",result.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Preview_EnforcesDecodedSampleAndWireBudgets()
    {
        await using var api=await PrepareAsync(maxBytes:1024);
        using var decoded=await api.WriteAsync(HttpMethod.Post,Endpoint,Request(api,"{}"+new string(' ',1023))); Assert.Equal(HttpStatusCode.RequestEntityTooLarge,decoded.StatusCode);
        var source=JsonSerializer.Serialize(Request(api,"{}"))+new string(' ',6*1024+65536);
        using var request=new HttpRequestMessage(HttpMethod.Post,Endpoint) {Content=new StringContent(source,Encoding.UTF8,"application/json")}; request.Headers.Add("X-CSRF-Token",await api.CsrfAsync());
        using var wire=await api.Client.SendAsync(request); Assert.Equal(HttpStatusCode.RequestEntityTooLarge,wire.StatusCode);
        using var recovered=await api.WriteAsync(HttpMethod.Post,Endpoint,Request(api)); recovered.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Preview_SecretCanariesNeverEnterApplicationLogsOrAudit()
    {
        var logs=new CapturedLogs(); await using var api=await PrepareAsync(logging:logs);
        const string sample="preview-sample-private-canary";const string constant="preview-config-private-canary";
        var config=Config.Replace("\"value\":null","\"value\":\""+constant+"\"");using var response=await api.WriteAsync(HttpMethod.Post,Endpoint,Request(api,"{\"s\":\""+sample+"\"}",config:config));response.EnsureSuccessStatusCode();
        Assert.DoesNotContain(logs.Messages,v=>v.Contains(sample)||v.Contains(constant));
        await using var db=api.Context();Assert.DoesNotContain(await db.Set<AuditLog>().Select(a=>a.AfterJson).ToArrayAsync(),v=>v is not null&&(v.Contains(sample)||v.Contains(constant)));
    }
    [Fact]
    public async Task Preview_Default16LeaseLimit_RejectsImmediatelyAndRecoversWithoutDoubleRelease()
    {
        await using var api=await PrepareAsync();using var scope=api.Services();var budget=scope.ServiceProvider.GetRequiredService<BodyTransformBudget>();
        var held=new List<BodyTransformBudget.Lease>();
        try {
            for(var i=0;i<16;i++){Assert.True(budget.TryAcquire(out var lease));Assert.NotNull(lease);held.Add(lease);}
            Assert.False(budget.TryAcquire(out var extra));Assert.Null(extra);
            using var rejected=await api.WriteAsync(HttpMethod.Post,Endpoint,Request(api));Assert.Equal(HttpStatusCode.ServiceUnavailable,rejected.StatusCode);
            using var result=JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());Assert.Equal("transform_body_busy",result.RootElement.GetProperty("reason").GetString());
            held[0].Dispose();held[0].Dispose();Assert.True(budget.TryAcquire(out var reused));Assert.NotNull(reused);
            Assert.False(budget.TryAcquire(out _));reused.Dispose();
        } finally {foreach(var lease in held)lease.Dispose();}
        using var recovered=await api.WriteAsync(HttpMethod.Post,Endpoint,Request(api));recovered.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Preview_StrictEnvelopeAndDisabledDirection_DoNotLeakLeases()
    {
        await using var api=await PrepareAsync();
        var source=JsonSerializer.Serialize(Request(api));source=source[..^1]+",\"direction\":\"Request\"}";
        using(var request=new HttpRequestMessage(HttpMethod.Post,Endpoint){Content=new StringContent(source,Encoding.UTF8,"application/json")}) {
            request.Headers.Add("X-CSRF-Token",await api.CsrfAsync());using var duplicate=await api.Client.SendAsync(request);Assert.Equal(HttpStatusCode.UnprocessableEntity,duplicate.StatusCode);
        }
        var config=Config.Replace("\"responseBody\":{\"statusCodes\":[200],\"maxBodyBytes\":1024,\"operations\":[{\"op\":\"move\",\"from\":\"/data\",\"path\":\"/result\"}]}","\"responseBody\":null");
        using var disabled=await api.WriteAsync(HttpMethod.Post,Endpoint,Request(api,"{}","Response",config));Assert.Equal(HttpStatusCode.UnprocessableEntity,disabled.StatusCode);
        for(var i=0;i<20;i++) {using var recovered=await api.WriteAsync(HttpMethod.Post,Endpoint,Request(api));recovered.EnsureSuccessStatusCode();}
    }

    [Theory]
    [InlineData(0)][InlineData(65)]
    public void Preview_BudgetOutOfRange_IsRejected(int value)=>Assert.Throws<ArgumentOutOfRangeException>(()=>new BodyTransformBudget(value));

    [Fact]
    public async Task Preview_OrganizationSharedRead_UsesActiveProjectReadOnlyGrant()
    {
        await using var api=await PrepareAsync(readOnly:true);
        await using(var db=api.Context()) {
            await db.Set<UserProjectScope>().Where(s=>s.UserId==api.User.Id).ExecuteDeleteAsync();
            db.Add(new UserProjectScope {UserId=api.User.Id,OrganizationId=api.Organization.Id,ProjectId=api.Project.Id,AccessMode="read_only"});await db.SaveChangesAsync();
        }
        using var response=await api.WriteAsync(HttpMethod.Post,Endpoint,Request(api));response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Preview_AbortedSlowUpload_ReleasesActualAdmissionLease()
    {
        var logs=new CapturedLogs();await using var api=await PrepareAsync(maxConcurrent:1,logging:logs);using var cancellation=new CancellationTokenSource();using var content=new PendingJsonContent();
        using var request=new HttpRequestMessage(HttpMethod.Post,Endpoint){Content=content};request.Headers.Add("X-CSRF-Token",await api.CsrfAsync());
        var pending=api.Client.SendAsync(request,cancellation.Token);
        await content.PrefixSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await logs.FirstPreviewEndpoint.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var leased=false;
        for(var i=0;i<50;i++) {
            using var probe=await api.WriteAsync(HttpMethod.Post,Endpoint,Request(api));
            if(probe.StatusCode==HttpStatusCode.ServiceUnavailable){leased=true;break;} await Task.Delay(20);
        }
        Assert.True(leased,"The slow real HTTP upload must hold the sole admission lease. Pending="+pending.Status+"; stages="+string.Join(" | ",logs.Messages.Where(m=>m.Contains("preview")||m.Contains("antiforgery",StringComparison.OrdinalIgnoreCase)||m.Contains("failed",StringComparison.OrdinalIgnoreCase))));
        cancellation.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await pending);
        var released=false;
        for(var i=0;i<50;i++) {using var response=await api.WriteAsync(HttpMethod.Post,Endpoint,Request(api));if(response.IsSuccessStatusCode){released=true;break;}await Task.Delay(20);}
        Assert.True(released,"Client cancellation must release the server lease.");
    }
    private sealed class PendingJsonContent:HttpContent
    {
        public TaskCompletionSource PrefixSent {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PendingJsonContent(){Headers.ContentType=new("application/json");}
        protected override bool TryComputeLength(out long length){length=0;return false;}
        protected override Task SerializeToStreamAsync(Stream stream,TransportContext? context)=>SerializeToStreamAsync(stream,context,CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream,TransportContext? context,CancellationToken ct)
        {await stream.WriteAsync(Encoding.UTF8.GetBytes("{"),ct);await stream.FlushAsync(ct);PrefixSent.TrySetResult();await Task.Delay(Timeout.Infinite,ct);}
    }

    private sealed class CapturedLogs : ILoggerProvider
    {
        public System.Collections.Concurrent.ConcurrentBag<string> Messages {get;}=[];
        public TaskCompletionSource FirstPreviewEndpoint {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ILogger CreateLogger(string categoryName)=>new Logger(Messages,FirstPreviewEndpoint);
        public void Dispose(){}
        private sealed class Logger(System.Collections.Concurrent.ConcurrentBag<string> messages,TaskCompletionSource endpoint):ILogger
        { public IDisposable? BeginScope<TState>(TState state) where TState:notnull=>null;public bool IsEnabled(LogLevel level)=>true;public void Log<TState>(LogLevel level,EventId id,TState state,Exception? exception,Func<TState,Exception?,string> formatter) { var message=formatter(state,exception); messages.Add(message); if(message.Contains("Executing endpoint",StringComparison.Ordinal)&&message.Contains("/policies/transform/preview",StringComparison.Ordinal))endpoint.TrySetResult(); } }
    }
}
