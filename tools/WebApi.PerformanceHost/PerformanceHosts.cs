using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Runtime;
using WebApi.ControlPlane;
using WebApi.Domain.Runtime;
using WebApi.Gateway;
using WebApi.Gateway.Configuration;
using WebApi.Infrastructure.Persistence;

namespace WebApi.PerformanceHost;
public static class PerformanceHosts
{
    public static WebApplication Build(string mode,IReadOnlyDictionary<string,string?> configuration)
    {
        var explainEnabled=configuration.GetValueOrDefault("Performance:ExplainEnabled")=="true";
        if(explainEnabled&&(mode!="controlplane"||configuration.GetValueOrDefault("Performance:DiagnosticMode")!="explain"))throw new InvalidDataException("EXPLAIN requires an explicit control-plane diagnostic window.");
        ExplainQueryProbe? explain=null;
        if(explainEnabled){
            var connection=configuration.GetValueOrDefault("ConnectionStrings:WebApi")??throw new InvalidDataException("Diagnostic database required.");
            using var schema=new WebApiDbContext(new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(connection).Options);
            var entities=schema.Model.GetEntityTypes().ToArray();
            var relations=entities.Select(e=>e.GetTableName()).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var indexes=entities.SelectMany(e=>e.GetIndexes().Select(i=>i.GetDatabaseName()).Concat(e.GetKeys().Select(k=>k.GetName()))).OfType<string>().ToHashSet(StringComparer.Ordinal);
            explain=new(connection,relations,indexes);
        }
        var audit=mode=="controlplane"&&configuration.GetValueOrDefault("Performance:SqlAuditDirectory") is { } auditDirectory?new PrivateSqlShapeAudit(auditDirectory):null;
        var probe=new RequestSqlProbe(audit:audit);
        void Configure(WebApplicationBuilder builder) {
            builder.Configuration.AddInMemoryCollection(configuration);builder.Logging.ClearProviders();
            if(mode!="backend"||configuration.ContainsKey("Performance:PrivateDirectory")) {
                builder.Services.AddSingleton(probe);builder.Services.AddSingleton(new PerformanceDiagnostics(explain));builder.Services.AddSingleton<IStartupFilter,PerformanceStartupFilter>();
                if(mode=="controlplane")builder.Services.ConfigureDbContext<WebApiDbContext>(options=>{options.AddInterceptors(probe);if(explain is not null)options.AddInterceptors(explain);});
            }
        }
        if(mode=="gateway")return GatewayApp.Build(["--environment","Development"],Configure);
        if(mode=="controlplane")return ControlPlaneApp.Build(["--environment","Development"],Configure);
        if(mode!="backend")throw new ArgumentException("Invalid performance host mode.");
        var builder=WebApplication.CreateBuilder(["--environment","Development"]);Configure(builder);var app=builder.Build();
        var body=Enumerable.Repeat((byte)'x',1024).ToArray();
        app.Run(async context=>{context.Response.ContentType="application/octet-stream";context.Response.ContentLength=body.Length;await context.Response.Body.WriteAsync(body,context.RequestAborted);});return app;
    }
}
internal sealed record PerformanceDiagnostics(ExplainQueryProbe? Explain);
internal sealed class PerformanceStartupFilter(IConfiguration configuration,RequestSqlProbe probe,PerformanceDiagnostics diagnostics):IStartupFilter
{
    private readonly string directory=Path.GetFullPath(configuration["Performance:PrivateDirectory"]??throw new InvalidOperationException("Private performance directory required."));
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)=>app=>{
        var token=ReadPrivate(Path.Combine(directory,"probe-token"),256);
        app.Use(async(context,following)=>{
            var supplied=context.Request.Headers["X-Performance-Token"].ToString();
            var authorized=supplied.Length==token.Length&&CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(supplied),Encoding.ASCII.GetBytes(token));
            context.Request.Headers.Remove("X-Performance-Token");
            if(context.Request.Path.StartsWithSegments("/__perf")) {
                if(!authorized){context.Response.StatusCode=404;return;}
                context.Response.Headers.CacheControl="no-store";
                if(context.Request.Method=="GET"&&context.Request.Path=="/__perf/state") {
                    using var process=Process.GetCurrentProcess();var info=GC.GetGCMemoryInfo();
                    await context.Response.WriteAsJsonAsync(new {heapBytes=GC.GetTotalMemory(false),lastGcHeapBytes=info.HeapSizeBytes,totalAvailableMemoryBytes=info.TotalAvailableMemoryBytes,workingSetBytes=process.WorkingSet64,totalAllocatedBytes=GC.GetTotalAllocatedBytes(false),pauseTicks=GC.GetTotalPauseDuration().Ticks,collections=new[]{GC.CollectionCount(0),GC.CollectionCount(1),GC.CollectionCount(2)},cpuTicks=process.TotalProcessorTime.Ticks,serverGc=System.Runtime.GCSettings.IsServerGC,processorCount=Environment.ProcessorCount},context.RequestAborted);return;
                }
                if(context.Request.Method=="GET"&&context.Request.Path.StartsWithSegments("/__perf/sql",out var remaining)) {
                    var id=remaining.Value?.TrimStart('/')??"";
                    if(id.Length is <1 or >128){context.Response.StatusCode=400;return;}
                    await context.Response.WriteAsJsonAsync(new {stats=probe.Read(id),shapes=probe.ReadShapes(id)},context.RequestAborted);return;
                }
                if(context.Request.Method=="GET"&&context.Request.Path.StartsWithSegments("/__perf/explain",out var planRemaining)&&diagnostics.Explain is { } explain){
                    var id=planRemaining.Value?.TrimStart('/')??"";if(id.Length is <1 or >128){context.Response.StatusCode=400;return;}
                    await context.Response.WriteAsJsonAsync(explain.Read(id),context.RequestAborted);return;
                }
                if(context.Request.Method=="POST"&&context.Request.Path=="/__perf/apply"&&context.RequestServices.GetService<SnapshotActivation>() is { } activation) {
                    var stage="request-parse";ApplyRequest? request=null;byte[]? bytes=null;
                    try {
                        request=await context.Request.ReadFromJsonAsync<ApplyRequest>(context.RequestAborted);
                        if(request is null||request.Sequence<=0||request.Profile is not ("anonymous" or "apikey" or "rs256" or "es256" or "no-grant")){context.Response.StatusCode=400;return;}
                        var file=Path.Combine(directory,request.Profile+".json");stage="file-check";CheckPrivate(file,16*1024*1024);
                        stage="file-read";bytes=await File.ReadAllBytesAsync(file,context.RequestAborted);
                        stage="snapshot-parse";var settings=context.RequestServices.GetRequiredService<GatewaySettings>();var snapshot=SnapshotValidator.ParsePayload(bytes,settings.EnvironmentId);
                        var envelope=new SnapshotEnvelope(Guid.NewGuid(),request.Sequence,snapshot.ConfigVersion,Convert.ToHexStringLower(SHA256.HashData(bytes)),bytes.Length);
                        stage="activate";var result=await activation.ApplyAsync(envelope,bytes,context.RequestAborted);
                        context.Response.StatusCode=result.Applied?200:409;await context.Response.WriteAsJsonAsync(result,context.RequestAborted);return;
                    }catch(Exception error)when(error is JsonException or IOException or InvalidDataException or ApiException or ArgumentException) {
                        var retained=await RetainActivationFailure(directory,stage,error,request?.Sequence,bytes);
                        context.Response.StatusCode=400;await context.Response.WriteAsJsonAsync(new {stage,errorType=error.GetType().Name,diagnosticRetained=retained},context.RequestAborted);return;
                    }
                }
                context.Response.StatusCode=404;return;
            }
            if(!authorized){await following();return;}
            var requestId=Guid.NewGuid().ToString("N");context.Response.Headers["X-Performance-Request-Id"]=requestId;
            var diagnose=context.Request.Method=="GET"&&context.Request.Path.StartsWithSegments("/api/v1")&&!context.Request.Path.StartsWithSegments("/api/v1/auth");
            using(probe.Begin(requestId))using(diagnose?diagnostics.Explain?.Begin(requestId):null)await following();
        });next(app);
    };
    private static async Task<bool> RetainActivationFailure(string directory,string stage,Exception error,long? sequence,byte[]? payload)
    {
        try {
            var name="activation-failure-"+Guid.NewGuid().ToString("N");
            string? payloadFile=null,payloadSha256=null;
            if(payload is not null) {
                // Capture the exact buffer consumed by this failed activation, not
                // the fixture file's possibly changed contents. This stays private.
                payloadFile=name+".bin";payloadSha256=Convert.ToHexStringLower(SHA256.HashData(payload));
                await using var payloadStream=new FileStream(Path.Combine(directory,payloadFile),FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,FileOptions.Asynchronous);
                if(!OperatingSystem.IsWindows())File.SetUnixFileMode(Path.Combine(directory,payloadFile),UnixFileMode.UserRead|UnixFileMode.UserWrite);
                await payloadStream.WriteAsync(payload);
            }
            var file=Path.Combine(directory,name+".json");
            await using var stream=new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,FileOptions.Asynchronous);
            if(!OperatingSystem.IsWindows())File.SetUnixFileMode(file,UnixFileMode.UserRead|UnixFileMode.UserWrite);
            await stream.WriteAsync(CanonicalJson.Serialize(new {stage,errorType=error.GetType().Name,sequence,time=DateTimeOffset.UtcNow,exception=error.ToString(),payloadFile,payloadBytes=payload?.Length,payloadSha256}));return true;
        }catch(Exception diagnosticError)when(diagnosticError is IOException or UnauthorizedAccessException){return false;}
    }
    private static string ReadPrivate(string file,long maximum){CheckPrivate(file,maximum);var value=File.ReadAllText(file).Trim();if(value.Length is <32 or >128||value.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not '-' and not '_'))throw new InvalidDataException("Invalid private probe token.");return value;}
    private static void CheckPrivate(string file,long maximum) {
        var info=new FileInfo(file);if(!info.Exists)throw new InvalidDataException("Private fixture file is missing.");
        if(info.LinkTarget is not null)throw new InvalidDataException("Private fixture file is a symbolic link.");
        if(info.Length>maximum)throw new InvalidDataException("Private fixture file exceeds its size limit.");
        if(!OperatingSystem.IsWindows()&&(File.GetUnixFileMode(file)&~(UnixFileMode.UserRead|UnixFileMode.UserWrite))!=0)throw new InvalidDataException("Fixture file is not private.");
    }
    private sealed record ApplyRequest(string Profile,long Sequence);
}
