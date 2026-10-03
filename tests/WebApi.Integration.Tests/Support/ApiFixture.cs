using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebApi.ControlPlane;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Integration.Tests.Support;
public sealed class ApiFixture : IAsyncDisposable
{
    public PostgresDatabase Database { get; } = new();
    public UserRecord User { get; } = new() { Username = "actor_"+Guid.NewGuid().ToString("N"), DisplayName="测试用户", SecurityStamp=Guid.NewGuid().ToString("N") };
    public string Password { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private WebApplication? app;
    public HttpClient Client { get; private set; } = null!;
    public WebApiDbContext Context() => Database.Context();
    public async Task InitializeAsync()
    {
        await Database.InitializeAsync();
        await using(var db=Context()) { await db.Database.MigrateAsync(); User.PasswordHash=new PasswordHasher<UserRecord>().HashPassword(User,Password); db.Add(User); await db.SaveChangesAsync(); }
        app=ControlPlaneApp.Build(["--environment","Development"],builder=>{
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration["ConnectionStrings:WebApi"]=Database.ConnectionString;
            builder.Logging.ClearProviders();
        });
        await app.StartAsync();
        var url=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        Client=new HttpClient(new HttpClientHandler { CookieContainer=new CookieContainer(), AllowAutoRedirect=false }) { BaseAddress=new Uri(url) };
    }
    public async Task<string> CsrfAsync()
    {
        using var response=await Client.GetAsync("/api/v1/auth/csrf");
        if(!response.IsSuccessStatusCode) return "missing-token";
        var body=await response.Content.ReadFromJsonAsync<Dictionary<string,string>>(); return body!["token"];
    }
    public async Task<HttpResponseMessage> LoginAsync(string? password=null)
    {
        var token=await CsrfAsync(); using var request=new HttpRequestMessage(HttpMethod.Post,"/api/v1/auth/login") {Content=JsonContent.Create(new {username=User.Username,password=password??Password})};
        request.Headers.Add("X-CSRF-Token",token); return await Client.SendAsync(request);
    }
    public async ValueTask DisposeAsync()
    {
        Client?.Dispose(); if(app is not null) {await app.StopAsync(); await app.DisposeAsync();} await Database.DisposeAsync();
    }
}
