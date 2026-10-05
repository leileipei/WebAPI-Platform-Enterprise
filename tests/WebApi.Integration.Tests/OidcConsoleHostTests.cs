using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebApi.ConsoleHost;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class OidcConsoleHostTests
{
    private static Uri Address(WebApplication app)=>new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
    [Fact] public async Task ConsoleHostForwardsOnlyOidcCallbacksAndCompletionStaysSpa()
    {
        var directory=Directory.CreateTempSubdirectory("console-proxy-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory.FullName,"index.html"),"<title>Fixture UI</title>");
            var builder=WebApplication.CreateBuilder(["--environment","Development"]);builder.WebHost.UseUrls("http://127.0.0.1:0");builder.Logging.ClearProviders();await using var upstream=builder.Build();
            upstream.MapGet("/auth/oidc/callback/{id:guid}",(HttpContext context)=>Results.Text("callback:"+context.Request.Host,statusCode:418));
            await upstream.StartAsync();
            await using var frontend=ConsoleHostApp.Build(["--environment","Development"],settings=>{
                settings.WebHost.UseUrls("http://127.0.0.1:0");settings.Logging.ClearProviders();
                settings.Configuration["Console:DistDirectory"]=directory.FullName;settings.Configuration["Console:ControlPlaneUrl"]=Address(upstream).ToString();
            });await frontend.StartAsync();using var client=new HttpClient{BaseAddress=Address(frontend)};
            using var response=await client.GetAsync("/auth/oidc/callback/"+Guid.NewGuid()+"?code=synthetic&state=synthetic");
            Assert.Equal((HttpStatusCode)418,response.StatusCode);Assert.Equal("callback:"+client.BaseAddress.Authority,await response.Content.ReadAsStringAsync());
            Assert.Contains("Fixture UI",await client.GetStringAsync("/auth/sso/complete"));
            Assert.Contains("Fixture UI",await client.GetStringAsync("/auth/oidc/other"));
            Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync("/api/unknown")).StatusCode);
            await frontend.StopAsync();await upstream.StopAsync();
        }
        finally{directory.Delete(true);}
    }
}
