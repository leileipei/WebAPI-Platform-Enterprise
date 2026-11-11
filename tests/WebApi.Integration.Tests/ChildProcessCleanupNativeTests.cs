using System.Diagnostics;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

// Uses the POSIX shell available on Ubuntu CI and macOS; no Windows qualification.
public sealed class ChildProcessCleanupNativeTests
{
    private sealed class OwnedChild : IAsyncDisposable
    {
        private readonly Process process=new();
        private bool started;
        private bool released;
        private bool failDeletionOnce;
        public string DirectoryPath {get;}=Path.Combine(Path.GetTempPath(),"credential-cleanup-native-"+Guid.NewGuid().ToString("N"));
        public ChildProcessCleanup Cleanup {get;}
        public int KillCalls,WaitCalls,ReleaseCalls,DeleteCalls;
        public (int,int,int,int) Calls=>(KillCalls,WaitCalls,ReleaseCalls,DeleteCalls);
        private OwnedChild(bool failDeletionOnce)
        {
            this.failDeletionOnce=failDeletionOnce;
            Cleanup=new(()=>!started||process.HasExited,
                ()=>{KillCalls++;process.Kill(entireProcessTree:true);},
                ()=>{WaitCalls++;return process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));},
                ()=>{Assert.True(!started||process.HasExited);process.Dispose();released=true;ReleaseCalls++;},
                ()=>
                {
                    Assert.True(released);DeleteCalls++;
                    if(this.failDeletionOnce){this.failDeletionOnce=false;throw new IOException("synthetic first directory deletion failure");}
                    if(Directory.Exists(DirectoryPath))Directory.Delete(DirectoryPath,true);
                    Assert.False(Directory.Exists(DirectoryPath));
                });
        }
        public static async Task<OwnedChild> CreateAsync(bool start=true,bool failDeletionOnce=false,Action<string>? beforeStart=null)
        {
            var child=new OwnedChild(failDeletionOnce);
            try
            {
                Directory.CreateDirectory(child.DirectoryPath);
                await File.WriteAllTextAsync(Path.Combine(child.DirectoryPath,"owned.txt"),"test-owned");
                beforeStart?.Invoke(child.DirectoryPath);
                if(!start)return child;
                Assert.True(File.Exists("/bin/sh"));
                child.process.StartInfo=new ProcessStartInfo("/bin/sh")
                {
                    UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,
                    WorkingDirectory=child.DirectoryPath
                };
                child.process.StartInfo.ArgumentList.Add("-c");
                child.process.StartInfo.ArgumentList.Add("printf 'cleanup-ready\\n'; read cleanup_exit");
                child.started=child.process.Start();Assert.True(child.started);
                Assert.False(child.process.Id==System.Environment.ProcessId);
                Assert.Equal("cleanup-ready",await child.process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.False(child.process.HasExited);
                return child;
            }
            catch
            {
                // The deletion fault belongs to the handed-off test case, never to failed setup cleanup.
                child.failDeletionOnce=false;
                await child.DisposeAsync();throw;
            }
        }
        public async Task RequestNaturalExitAsync()
        {
            await process.StandardInput.WriteLineAsync("exit");await process.StandardInput.FlushAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(process.HasExited);Assert.Equal(0,process.ExitCode);
        }
        public ValueTask DisposeAsync()=>Cleanup.DisposeAsync();
    }

    [Fact]
    public async Task Native_live_child_exits_before_handle_release_and_directory_deletion()
    {
        await using var child=await OwnedChild.CreateAsync();
        await child.Cleanup.DisposeAsync();await child.Cleanup.DisposeAsync();
        Assert.Equal((1,1,1,1),child.Calls);Assert.False(Directory.Exists(child.DirectoryPath));
    }
    [Fact]
    public async Task Native_already_exited_child_is_released_without_kill_or_wait()
    {
        await using var child=await OwnedChild.CreateAsync();
        await child.RequestNaturalExitAsync();await child.Cleanup.DisposeAsync();
        Assert.Equal((0,0,1,1),child.Calls);Assert.False(Directory.Exists(child.DirectoryPath));
    }
    [Fact]
    public async Task Native_unstarted_process_does_not_require_liveness_or_stop()
    {
        await using var child=await OwnedChild.CreateAsync(start:false);
        await child.Cleanup.DisposeAsync();
        Assert.Equal((0,0,1,1),child.Calls);Assert.False(Directory.Exists(child.DirectoryPath));
    }
    [Fact]
    public async Task Native_setup_failure_keeps_original_error_and_removes_its_directory()
    {
        string? directory=null;
        var error=new InvalidOperationException("synthetic setup failure before process start");
        try
        {
            var actual=await Assert.ThrowsAsync<InvalidOperationException>(()=>OwnedChild.CreateAsync(failDeletionOnce:true,
                beforeStart:path=>{directory=path;throw error;}));
            Assert.Same(error,actual);Assert.False(Directory.Exists(directory!));
        }
        finally
        {
            // Retain the failed assertion while removing only this test's known, unstarted fixture directory.
            if(directory is not null&&Directory.Exists(directory))Directory.Delete(directory,true);
        }
    }
    [Fact]
    public async Task Native_directory_retry_does_not_access_a_released_process()
    {
        await using var child=await OwnedChild.CreateAsync(failDeletionOnce:true);
        await Assert.ThrowsAsync<IOException>(()=>child.Cleanup.DisposeAsync().AsTask());
        Assert.Equal((1,1,1,1),child.Calls);Assert.True(Directory.Exists(child.DirectoryPath));
        await child.Cleanup.DisposeAsync();
        Assert.Equal((1,1,1,2),child.Calls);Assert.False(Directory.Exists(child.DirectoryPath));
    }
}
