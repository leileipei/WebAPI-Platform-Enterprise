using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace WebApi.Integration.Tests.Support;

internal sealed class ChildControlPlane : IAsyncDisposable
{
    private readonly Process process;
    private readonly string directory;
    private bool started;
    private bool disposed;
    public HttpClient Client {get;private set;}=null!;
    public string ApplicationName {get;}
    public int ProcessId=>process.Id;

    private ChildControlPlane(Process process,string directory,string applicationName)
    {this.process=process;this.directory=directory;ApplicationName=applicationName;}

    public static async Task<ChildControlPlane> StartAsync(ApiFixture api)
    {
        var root=Environment.GetEnvironmentVariable("WEBAPI_SOURCE_ROOT")
            ??throw new InvalidOperationException("Cross-process tests require the existing isolated source root.");
        var dll=Path.Combine(root,"src","WebApi.ControlPlane","bin","Release","net10.0","WebApi.ControlPlane.dll");
        if(!File.Exists(dll)||!File.Exists(Path.ChangeExtension(dll,"runtimeconfig.json")))
            throw new InvalidOperationException("The already-built control-plane entrypoint is required; this helper does not build or download it.");
        var directory=Path.Combine(Path.GetTempPath(),"credential-child-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        if(!OperatingSystem.IsWindows())File.SetUnixFileMode(directory,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        var applicationName="credential-child-"+Guid.NewGuid().ToString("N");
        var connection=new NpgsqlConnectionStringBuilder(api.Database.ConnectionString){ApplicationName=applicationName};
        var start=new ProcessStartInfo("dotnet")
        {UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=directory};
        foreach(var value in new[]{dll,"--environment","Development","--urls","http://127.0.0.1:0"})start.ArgumentList.Add(value);
        start.Environment["ConnectionStrings__WebApi"]=connection.ConnectionString;
        start.Environment["Authentication__LoginProtection__HmacSecretFile"]=Path.Combine(api.LoginProtectionDirectory,"login-protection-hmac");
        start.Environment["Authentication__LoginProtection__RedisPrefix"]="test-login-"+api.LoginProtectionDeploymentId;
        start.Environment["Redis__Connection"]=Environment.GetEnvironmentVariable("WEBAPI_TEST_REDIS")??"redis:6379";
        start.Environment["DataProtection__KeysDirectory"]=Path.Combine(directory,"keys");
        start.Environment["DataProtection__ApplicationName"]=applicationName;
        start.Environment["VerificationReports__Directory"]=Path.Combine(directory,"reports");
        start.Environment["Logging__LogLevel__Default"]="Warning";
        start.Environment["Logging__LogLevel__Microsoft.Hosting.Lifetime"]="Information";
        start.Environment["Logging__Console__FormatterName"]="simple";
        start.Environment["Logging__Console__FormatterOptions__SingleLine"]="true";
        var process=new Process{StartInfo=start,EnableRaisingEvents=true};
        var child=new ChildControlPlane(process,directory,applicationName);
        var ready=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recent=new ConcurrentQueue<string>();
        void Capture(string? line)
        {
            if(line is null)return;
            foreach(var secret in new[]{api.Database.ConnectionString,connection.Password,api.Password})
                if(!string.IsNullOrEmpty(secret))line=line.Replace(secret,"[redacted]",StringComparison.Ordinal);
            recent.Enqueue(line.Length>1024?line[..1024]:line);
            while(recent.Count>32)recent.TryDequeue(out _);
            var match=Regex.Match(line,@"Now listening on: (http://127\.0\.0\.1:[0-9]+)");
            if(match.Success)ready.TrySetResult(match.Groups[1].Value);
        }
        process.OutputDataReceived+=(_,e)=>Capture(e.Data);
        process.ErrorDataReceived+=(_,e)=>Capture(e.Data);
        process.Exited+=(_,_)=>ready.TrySetException(new InvalidOperationException("Isolated control plane exited before readiness."));
        try
        {
            child.started=process.Start();
            if(!child.started)throw new InvalidOperationException("Cannot start the isolated control plane.");
            process.BeginOutputReadLine();process.BeginErrorReadLine();
            var url=await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            child.Client=new HttpClient(new HttpClientHandler{CookieContainer=new CookieContainer(),AllowAutoRedirect=false})
                {BaseAddress=new Uri(url),Timeout=TimeSpan.FromSeconds(15)};
            using var health=await child.Client.GetAsync("/health/live");health.EnsureSuccessStatusCode();
            var csrf=await child.Client.GetFromJsonAsync<JsonElement>("/api/v1/auth/csrf");
            using var request=new HttpRequestMessage(HttpMethod.Post,"/api/v1/auth/login")
                {Content=JsonContent.Create(new{username=api.User.Username,password=api.Password})};
            request.Headers.Add("X-CSRF-Token",csrf.GetProperty("token").GetString());
            using var login=await child.Client.SendAsync(request);login.EnsureSuccessStatusCode();
            return child;
        }
        catch(Exception error)
        {
            await child.DisposeAsync();
            throw new InvalidOperationException("Isolated control-plane startup failed. "+string.Join(" | ",recent.TakeLast(8)),error);
        }
    }

    public async Task AssertWaitingAsync(ApiFixture api)
    {
        await using var connection=await api.Database.OpenAsync();
        await using var query=new NpgsqlCommand("""
            SELECT count(*)::int FROM pg_locks l JOIN pg_stat_activity a ON a.pid=l.pid
            WHERE l.locktype='advisory' AND l.classid=0 AND l.objid=8901202 AND l.objsubid=1
              AND NOT l.granted AND a.datname=current_database() AND a.application_name=@app
            """,connection);
        query.Parameters.AddWithValue("app",ApplicationName);
        if((int)(await query.ExecuteScalarAsync())! != 1)
            throw new InvalidOperationException("The real child process must have one command queued on the fixture governance lock.");
        if(process.HasExited)throw new InvalidOperationException("The isolated control plane exited during its request.");
    }

    public async ValueTask DisposeAsync()
    {
        if(disposed)return;disposed=true;
        Client?.Dispose();
        if(started&&!process.HasExited)
        {
            process.Kill(entireProcessTree:true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        if(started&&!process.HasExited)throw new InvalidOperationException("Isolated control-plane process did not exit.");
        process.Dispose();
        Directory.Delete(directory,true);
        if(Directory.Exists(directory))throw new InvalidOperationException("Isolated control-plane temporary directory was retained.");
    }
}
