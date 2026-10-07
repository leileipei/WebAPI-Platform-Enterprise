using System.Collections.Concurrent;
var b=WebApplication.CreateBuilder(args);b.WebHost.UseUrls("http://0.0.0.0:18080","http://0.0.0.0:18081");var app=b.Build();
var counts=new ConcurrentDictionary<string,int>();int failures=0;bool cacheable=true;
app.MapGet("/health",()=>Results.Ok());
app.MapPost("/__fixture/config",async(HttpContext c)=>{var v=await c.Request.ReadFromJsonAsync<Config>();Interlocked.Exchange(ref failures,v!.Failures);cacheable=v.Cacheable;return Results.Ok();});
app.MapGet("/__fixture/stats",()=>Results.Json(counts));
app.Map("/{**path}",async(HttpContext c)=>{
 var key=c.Request.Path.ToString()+c.Request.QueryString;var count=counts.AddOrUpdate(key,1,(_,n)=>n+1);
 if(c.Request.Path.ToString().EndsWith("/__abort")){c.Abort();return;}
 if(c.Request.Path.ToString().EndsWith("/__hold"))await Task.Delay(1500,c.RequestAborted);
 var remaining=Interlocked.Decrement(ref failures);if(remaining>=0)c.Response.StatusCode=503;else if(cacheable)c.Response.Headers.CacheControl="public, max-age=120";
 c.Response.Headers["X-Fixture-Call"]=count.ToString();await c.Response.WriteAsJsonAsync(new{call=count,port=c.Connection.LocalPort,receivedApiKey=c.Request.Headers.ContainsKey("X-API-Key"),receivedBearer=c.Request.Headers.Authorization.ToString().StartsWith("Bearer ")});
});await app.RunAsync();
record Config(int Failures,bool Cacheable);
