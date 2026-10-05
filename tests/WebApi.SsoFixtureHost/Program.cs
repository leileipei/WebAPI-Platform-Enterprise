// Owned test-only backchannel proxy. Synthetic credentials never leave the fixture network.
var builder=WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
var app=builder.Build();
var target=builder.Configuration["FixtureTarget"]??throw new InvalidOperationException("Fixture target required.");
var observe=builder.Configuration["FixtureObserve"]=="true";
using var observationLock=new SemaphoreSlim(1,1);
using var client=new HttpClient(new SocketsHttpHandler{AllowAutoRedirect=false,UseProxy=false,UseCookies=false}){Timeout=Timeout.InfiniteTimeSpan};
app.Run(async context=>{
    if(observe)
    {
        var path=context.Request.Path.Value??"";
        var kind=path.StartsWith("/auth/oidc/callback/",StringComparison.Ordinal)?"callback":path=="/auth/sso/complete"?"complete":path=="/api/v1/auth/me"?"me":path.EndsWith("/start",StringComparison.Ordinal)&&path.StartsWith("/api/v1/auth/sso/",StringComparison.Ordinal)?"start":null;
        if(kind is not null)
        {
            var names=context.Request.Cookies.Keys;
            var site=context.Request.Headers["Sec-Fetch-Site"].ToString();if(site is not ("cross-site" or "same-site" or "same-origin" or "none"))site="unknown";
            var record=System.Text.Json.JsonSerializer.Serialize(new{kind,method=context.Request.Method,fetchSite=site,
                correlation=names.Any(name=>name.StartsWith(".WebApi.Oidc.Correlation.",StringComparison.Ordinal)),nonce=names.Any(name=>name.StartsWith(".WebApi.Oidc.Nonce.",StringComparison.Ordinal)),
                platformSession=names.Contains(builder.Configuration["FixtureSessionCookie"]??"WebApi.Session"),csrf=names.Contains(builder.Configuration["FixtureCsrfCookie"]??"WebApi.Csrf")});
            await observationLock.WaitAsync(context.RequestAborted);
            try{await File.AppendAllTextAsync("/fixture-observations/cookie-requests.jsonl",record+"\n",context.RequestAborted);}finally{observationLock.Release();}
        }
    }
    if(context.Request.Path.Value?.EndsWith("/token",StringComparison.Ordinal)==true&&File.Exists("/fixture-flags/block-token"))
        await Task.Delay(Timeout.InfiniteTimeSpan,context.RequestAborted);
    using var request=new HttpRequestMessage(new HttpMethod(context.Request.Method),target+context.Request.Path+context.Request.QueryString);
    request.Headers.Host=context.Request.Host.Value;
    foreach(var name in new[]{"Cookie","Origin","Accept","User-Agent","X-CSRF-Token","Idempotency-Key","If-Match","If-None-Match","Sec-Fetch-Site","Sec-Fetch-Mode","Sec-Fetch-Dest"})
        if(context.Request.Headers.TryGetValue(name,out var values))request.Headers.TryAddWithoutValidation(name,values.ToArray());
    if(context.Request.ContentLength is >0||context.Request.Method=="POST")
    {request.Content=new StreamContent(context.Request.Body);if(context.Request.ContentType is {} type)request.Content.Headers.TryAddWithoutValidation("Content-Type",type);}
    if(observe&&context.Request.Method=="GET"&&context.Request.Path=="/api/v1/scope-tree"&&File.Exists("/fixture-observations/deny-scope-once"))
    {File.Delete("/fixture-observations/deny-scope-once");context.Response.StatusCode=403;await context.Response.WriteAsJsonAsync(new{title="Synthetic permission refresh"});return;}
    using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,context.RequestAborted);
    const string providerPrefix="/api/v1/settings/sso/providers/";
    if(observe&&context.Request.Method=="GET"&&context.Request.Path.Value is {} detailPath&&detailPath.StartsWith(providerPrefix,StringComparison.Ordinal)&&
        Guid.TryParse(detailPath[providerPrefix.Length..],out _)&&File.Exists("/fixture-observations/hold-detail"))
    {
        await File.WriteAllTextAsync("/fixture-observations/detail-held","true",context.RequestAborted);
        while(File.Exists("/fixture-observations/hold-detail"))await Task.Delay(100,context.RequestAborted);
    }
    if(observe&&context.Request.Method=="PUT"&&context.Request.Path.Value is {} writePath&&writePath.StartsWith(providerPrefix,StringComparison.Ordinal)&&
        Guid.TryParse(writePath[providerPrefix.Length..],out _)&&File.Exists("/fixture-observations/hold-command"))
    {
        await File.WriteAllTextAsync("/fixture-observations/command-held","true",context.RequestAborted);
        while(File.Exists("/fixture-observations/hold-command"))await Task.Delay(100,context.RequestAborted);
    }
    context.Response.StatusCode=(int)response.StatusCode;
    foreach(var header in response.Headers.Concat(response.Content.Headers))if(header.Key is not ("Transfer-Encoding" or "Connection"))context.Response.Headers[header.Key]=header.Value.ToArray();
    await response.Content.CopyToAsync(context.Response.Body,context.RequestAborted);
});
await app.RunAsync();
