namespace WebApi.TestBackend;
public sealed record BackendResponse(string BackendId,string Method,string Path,string TraceId,long DeploymentSequence,bool ReceivedApiKey);
public sealed class BackendProbe
{
    private int hold;private TaskCompletionSource<bool> started=new(TaskCreationOptions.RunContinuationsAsynchronously),released=new(TaskCreationOptions.RunContinuationsAsynchronously);public Task Started=>started.Task;
    public void HoldNext() {started=new(TaskCreationOptions.RunContinuationsAsynchronously);released=new(TaskCreationOptions.RunContinuationsAsynchronously);Interlocked.Exchange(ref hold,1);}
    public void Release()=>released.TrySetResult(true);
    public async Task ObserveAsync(CancellationToken ct) {if(Interlocked.Exchange(ref hold,0)==0) return;started.TrySetResult(true);await released.Task.WaitAsync(ct);}
}
public static class TestBackendApp
{
    public static WebApplication Build(string[] args,Action<WebApplicationBuilder>? configure=null)
    {var b=WebApplication.CreateBuilder(args);configure?.Invoke(b);b.Services.AddSingleton<BackendProbe>();var app=b.Build();var id=b.Configuration["Backend:Id"]??"test-backend";app.MapGet("/health",()=>Results.Ok(new {status="healthy"}));app.Map("/{**path}",async(HttpContext ctx,BackendProbe probe)=>{await probe.ObserveAsync(ctx.RequestAborted);long.TryParse(ctx.Request.Headers["X-WebApi-Deployment-Sequence"],out var sequence);await ctx.Response.WriteAsJsonAsync(new BackendResponse(id,ctx.Request.Method,ctx.Request.Path,ctx.Request.Headers["X-WebApi-Trace-Id"].ToString(),sequence,ctx.Request.Headers.ContainsKey("X-API-Key")));});return app;}
}
