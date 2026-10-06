using System.Diagnostics;
using System.Text.Json;
using WebApi.Infrastructure.Contracts;
using Xunit;
namespace WebApi.Domain.Tests;
public class SchemaProcessTests
{
    private static ContractProcessRequest Request=>new("schema",JsonSerializer.SerializeToElement(new {value=1}));
    private sealed class ToolFixture:IDisposable
    {
        public string Directory {get;}=Path.Combine(Path.GetTempPath(),"contract-process-"+Guid.NewGuid());
        public string Script=>Path.Combine(Directory,"fixture.sh");
        public string Pids=>Path.Combine(Directory,"pids");
        public ToolFixture(string body){System.IO.Directory.CreateDirectory(Directory);File.WriteAllText(Script,"#!/bin/sh\n"+body+"\n");if(!OperatingSystem.IsWindows())File.SetUnixFileMode(Script,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);}
        public void Dispose()=>System.IO.Directory.Delete(Directory,true);
        public ContractProcessRunner Runner=>new(new(Pids,Script));
    }
    [Fact]public async Task HungEvaluatorIsKilledWithinDeadlineAndSlotReleased()
    {
        using var f=new ToolFixture("echo $$ >> \"$1\"; sleep 300 & echo $! >> \"$1\"; wait");var runner=f.Runner;var watch=Stopwatch.StartNew();
        var timed=await runner.RunAsync(Request,TimeSpan.FromMilliseconds(400),default);
        Assert.Equal("Incomplete",timed.Status);Assert.Contains(timed.Issues,x=>x.Code=="contract_process_timeout");Assert.True(watch.Elapsed<TimeSpan.FromSeconds(4));
        var pids=File.ReadAllLines(f.Pids).Select(int.Parse).ToArray();Assert.Equal(2,pids.Length);
        foreach(var pid in pids){var path=$"/proc/{pid}/stat";if(File.Exists(path))Assert.Contains(") Z",File.ReadAllText(path));}
        File.WriteAllText(f.Script,"#!/bin/sh\nprintf '%s' '{\"status\":\"Valid\",\"issues\":[]}'\n");
        Assert.Equal("Valid",(await runner.RunAsync(Request,TimeSpan.FromSeconds(2),default)).Status);
    }
    [Fact]public async Task TwoActiveAndTwoWaitingRejectTheFifthRequest()
    {
        using var f=new ToolFixture("echo $$ >> \"$1\"; sleep 300");var runner=f.Runner;using var cancelled=new CancellationTokenSource();
        var calls=Enumerable.Range(0,4).Select(_=>runner.RunAsync(Request,TimeSpan.FromSeconds(3),cancelled.Token)).ToArray();
        try {for(var i=0;i<100&&(!File.Exists(f.Pids)||File.ReadAllLines(f.Pids).Length<2);i++)await Task.Delay(10);
            Assert.Equal(2,File.ReadAllLines(f.Pids).Length);
            var rejected=await runner.RunAsync(Request,TimeSpan.FromSeconds(1),default);Assert.Equal("Incomplete",rejected.Status);Assert.Contains(rejected.Issues,x=>x.Code=="contract_process_capacity");
        }finally{cancelled.Cancel();await Task.WhenAll(calls);}
    }
    [Fact]public async Task ChildNeverReceivesConnectionStringsOrCredentialEnvironment()
    {
        using var f=new ToolFixture("if [ -n \"$ConnectionStrings__Postgres$WEBAPI_TEST_SECRET\" ]; then printf '%s' '{\"status\":\"Invalid\",\"issues\":[]}'; else printf '%s' '{\"status\":\"Valid\",\"issues\":[]}'; fi");
        Environment.SetEnvironmentVariable("ConnectionStrings__Postgres","CANARY-CONNECTION");Environment.SetEnvironmentVariable("WEBAPI_TEST_SECRET","CANARY-SECRET");
        try{Assert.Equal("Valid",(await f.Runner.RunAsync(Request,TimeSpan.FromSeconds(2),default)).Status);}
        finally{Environment.SetEnvironmentVariable("ConnectionStrings__Postgres",null);Environment.SetEnvironmentVariable("WEBAPI_TEST_SECRET",null);}
    }
    [Fact]public async Task ActualBuiltRuntimeToolEvaluatesTheProtocol()
    {
        var path=Environment.GetEnvironmentVariable("WEBAPI_RUNTIME_TOOL_DLL");Assert.False(string.IsNullOrEmpty(path));Assert.True(File.Exists(path));
        var runner=new ContractProcessRunner(new(path!));
        var response=await runner.RunAsync(ContractProcessProtocol.SchemaRequest(SchemaEvaluationTests.Input("{\"type\":\"integer\"}","\"bad\"")),TimeSpan.FromSeconds(5),default);
        Assert.Equal("Invalid",response.Status);Assert.NotNull(response.Result);Assert.Empty(response.Issues);
    }
    [Fact]public async Task QueueDeadlineStartsBeforeSlotAcquisition()
    {
        using var f=new ToolFixture("echo $$ >> \"$1\"; sleep 300");var runner=f.Runner;using var cancel=new CancellationTokenSource();
        var first=runner.RunAsync(Request,TimeSpan.FromSeconds(4),cancel.Token);var second=runner.RunAsync(Request,TimeSpan.FromSeconds(4),cancel.Token);
        try {
            for(var i=0;i<100&&(!File.Exists(f.Pids)||File.ReadAllLines(f.Pids).Length<2);i++)await Task.Delay(10);
            Assert.Equal(2,File.ReadAllLines(f.Pids).Length);var watch=Stopwatch.StartNew();
            var queued=await runner.RunAsync(Request,TimeSpan.FromMilliseconds(200),default);
            Assert.Contains(queued.Issues,x=>x.Code=="contract_process_timeout");Assert.True(watch.Elapsed<TimeSpan.FromSeconds(1));Assert.Equal(2,File.ReadAllLines(f.Pids).Length);
        }finally{cancel.Cancel();await Task.WhenAll(first,second);}
    }
    [Fact]public async Task OutputOverflowTerminatesAndReleasesTheSlot()
    {
        using var f=new ToolFixture("head -c 8388609 /dev/zero");var runner=f.Runner;
        Assert.Contains((await runner.RunAsync(Request,TimeSpan.FromSeconds(2),default)).Issues,x=>x.Code=="contract_process_output_budget");
        File.WriteAllText(f.Script,"#!/bin/sh\nprintf '%s' '{\"status\":\"Valid\",\"issues\":[]}'\n");
        Assert.Equal("Valid",(await runner.RunAsync(Request,TimeSpan.FromSeconds(2),default)).Status);
    }
    [Fact]public async Task InputOverflowNeverStartsAChild()
    {
        using var f=new ToolFixture("echo $$ >> \"$1\"");var oversized=new ContractProcessRequest("schema",JsonSerializer.SerializeToElement(new string('x',16*1024*1024)));
        Assert.Contains((await f.Runner.RunAsync(oversized,TimeSpan.FromSeconds(2),default)).Issues,x=>x.Code=="contract_process_input_budget");Assert.False(File.Exists(f.Pids));
    }
}
