using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
var b=WebApplication.CreateBuilder(args);b.WebHost.UseUrls("http://0.0.0.0:18080","http://0.0.0.0:18081");var app=b.Build();
var counts=new ConcurrentDictionary<string,int>();var bodies=new ConcurrentDictionary<string,string>();var held=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int failures=0;bool cacheable=true;
app.MapGet("/health",()=>Results.Ok());
app.MapPost("/__fixture/config",async(HttpContext c)=>{var v=await c.Request.ReadFromJsonAsync<Config>();Interlocked.Exchange(ref failures,v!.Failures);cacheable=v.Cacheable;return Results.Ok();});
app.MapGet("/__fixture/stats",()=>Results.Json(counts));
app.MapGet("/__fixture/network/body",(string path)=>bodies.TryGetValue(path,out var body)?Results.Text(body,"application/json"):Results.NotFound());
app.MapPost("/__fixture/network/release",()=>{held.TrySetResult();return Results.Ok();});
app.Map("/{**path}",async(HttpContext c)=>{
 var key=c.Request.Path.ToString()+c.Request.QueryString;var count=counts.AddOrUpdate(key,1,(_,n)=>n+1);
 if(c.Request.Path.ToString().EndsWith("/__abort")){c.Abort();return;}
 if(c.Request.Path.ToString().EndsWith("/__hold"))await Task.Delay(1500,c.RequestAborted);
 if(c.Request.Path.ToString().EndsWith("/__gate"))await held.Task.WaitAsync(c.RequestAborted);
 var remaining=Interlocked.Decrement(ref failures);if(remaining>=0)c.Response.StatusCode=503;else if(cacheable)c.Response.Headers.CacheControl="public, max-age=120";
 c.Response.Headers["X-Fixture-Call"]=count.ToString();
 if(c.Request.Path.StartsWithSegments("/__network/echo")){
  using var reader=new StreamReader(c.Request.Body,new UTF8Encoding(false,true));var body=await reader.ReadToEndAsync(c.RequestAborted);
  if(string.IsNullOrEmpty(body))body=c.Request.Path.ToString().EndsWith("/invalid")?"{\"n\":9007199254740993,\"nil\":null,\"sensitive\":\"PRIVATE_RESPONSE_NETWORK\"}":"{\"n\":9007199254740993,\"nil\":null,\"items\":[\"upstream\"]}";
  bodies[key]=body;using var parsed=JsonDocument.Parse(body);using var output=new MemoryStream();using(var writer=new Utf8JsonWriter(output)){
   writer.WriteStartObject();writer.WriteString("method",c.Request.Method);writer.WriteString("path",c.Request.Path);writer.WriteString("query",c.Request.QueryString.ToString().TrimStart('?'));
   writer.WriteString("requestHeader",c.Request.Headers["X-Network-QA"].ToString());writer.WriteString("origin",c.Request.Headers.Origin.ToString());writer.WriteBoolean("receivedApiKey",c.Request.Headers.ContainsKey("X-API-Key"));
   writer.WritePropertyName("body");writer.WriteRawValue(parsed.RootElement.GetRawText());writer.WriteEndObject();
  }
  var bytes=output.ToArray();c.Response.Headers["X-Fixture-Decoded-Bytes"]=bytes.Length.ToString();c.Response.Headers.Vary="Origin";c.Response.ContentType="application/json";
  var encoding=c.Request.Path.ToString().Split('/').Last();
  if(encoding is "gzip" or "br"){
   using var encoded=new MemoryStream();using(Stream compressor=encoding=="gzip"?new GZipStream(encoded,CompressionLevel.SmallestSize,true):new BrotliStream(encoded,CompressionLevel.SmallestSize,true))await compressor.WriteAsync(bytes,c.RequestAborted);
   bytes=encoded.ToArray();c.Response.Headers.ContentEncoding=encoding;
  }
  c.Response.Headers["X-Fixture-Encoded-Bytes"]=bytes.Length.ToString();c.Response.ContentLength=bytes.Length;await c.Response.Body.WriteAsync(bytes,c.RequestAborted);return;
 }
 await c.Response.WriteAsJsonAsync(new{call=count,port=c.Connection.LocalPort,receivedApiKey=c.Request.Headers.ContainsKey("X-API-Key"),receivedBearer=c.Request.Headers.Authorization.ToString().StartsWith("Bearer ")});
});await app.RunAsync();
record Config(int Failures,bool Cacheable);
