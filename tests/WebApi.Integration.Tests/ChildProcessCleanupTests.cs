using System.ComponentModel;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class ChildProcessCleanupTests
{
    private sealed class Lifetime
    {
        public bool Exited;
        public bool ExitOnKill=true;
        public bool Released;
        public bool Cleaned;
        public Exception? KillError;
        public Exception? WaitError;
        public Exception? CleanupError;
        public int KillCalls,WaitCalls,ReleaseCalls,CleanupCalls;
        public (int,int,int,int) Calls=>(KillCalls,WaitCalls,ReleaseCalls,CleanupCalls);
        public ChildProcessCleanup Create()=>new(
            ()=>{if(Released)throw new ObjectDisposedException("process");return Exited;},
            ()=>{KillCalls++;if(ExitOnKill)Exited=true;if(KillError is not null)throw KillError;},
            ()=>{WaitCalls++;return WaitError is null?Task.CompletedTask:Task.FromException(WaitError);},
            ()=>{Assert.True(Exited);ReleaseCalls++;Released=true;},
            ()=>{Assert.True(Released);CleanupCalls++;if(CleanupError is not null){var error=CleanupError;CleanupError=null;throw error;}Cleaned=true;});
    }

    [Fact]
    public async Task Already_exited_process_releases_and_cleans_only_once()
    {
        var life=new Lifetime{Exited=true};var cleanup=life.Create();
        await cleanup.DisposeAsync();await cleanup.DisposeAsync();
        Assert.Equal((0,0,1,1),life.Calls);Assert.True(life.Cleaned);
    }
    [Fact]
    public async Task Live_process_is_stopped_and_confirmed_before_directory_cleanup()
    {
        var life=new Lifetime();await life.Create().DisposeAsync();
        Assert.Equal((1,1,1,1),life.Calls);Assert.True(life.Cleaned);
    }
    [Fact]
    public async Task Natural_exit_during_kill_invalid_operation_still_cleans()
    {
        var life=new Lifetime{KillError=new InvalidOperationException("exited during Kill")};
        await life.Create().DisposeAsync();
        Assert.Equal((1,1,1,1),life.Calls);Assert.True(life.Cleaned);
    }
    [Fact]
    public async Task Natural_exit_during_kill_os_error_still_cleans()
    {
        var life=new Lifetime{KillError=new Win32Exception(3,"process already exited")};
        await life.Create().DisposeAsync();
        Assert.Equal((1,1,1,1),life.Calls);Assert.True(life.Cleaned);
    }
    [Fact]
    public async Task Live_permission_error_preserves_directory_and_allows_retry()
    {
        var error=new Win32Exception(5,"synthetic permission denial");
        var life=new Lifetime{ExitOnKill=false,KillError=error};var cleanup=life.Create();
        Assert.Same(error,await Assert.ThrowsAsync<Win32Exception>(()=>cleanup.DisposeAsync().AsTask()));
        Assert.Equal((1,0,0,0),life.Calls);Assert.False(life.Cleaned);
        life.Exited=true;await cleanup.DisposeAsync();
        Assert.Equal((1,0,1,1),life.Calls);Assert.True(life.Cleaned);
    }
    [Fact]
    public async Task Wait_timeout_preserves_directory_and_allows_retry_after_exit()
    {
        var error=new TimeoutException("synthetic wait timeout");
        var life=new Lifetime{ExitOnKill=false,WaitError=error};var cleanup=life.Create();
        Assert.Same(error,await Assert.ThrowsAsync<TimeoutException>(()=>cleanup.DisposeAsync().AsTask()));
        Assert.Equal((1,1,0,0),life.Calls);Assert.False(life.Cleaned);
        life.Exited=true;await cleanup.DisposeAsync();
        Assert.Equal((1,1,1,1),life.Calls);Assert.True(life.Cleaned);
    }
    [Fact]
    public async Task Unproven_exit_does_not_release_or_delete_the_directory()
    {
        var life=new Lifetime{ExitOnKill=false};var cleanup=life.Create();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>cleanup.DisposeAsync().AsTask());
        Assert.Equal((1,1,0,0),life.Calls);Assert.False(life.Cleaned);
        life.Exited=true;await cleanup.DisposeAsync();
        Assert.Equal((1,1,1,1),life.Calls);Assert.True(life.Cleaned);
    }
    [Fact]
    public async Task Directory_failure_can_retry_without_touching_released_process()
    {
        var error=new IOException("synthetic directory failure");
        var life=new Lifetime{Exited=true,CleanupError=error};var cleanup=life.Create();
        Assert.Same(error,await Assert.ThrowsAsync<IOException>(()=>cleanup.DisposeAsync().AsTask()));
        Assert.Equal((0,0,1,1),life.Calls);Assert.False(life.Cleaned);
        await cleanup.DisposeAsync();
        Assert.Equal((0,0,1,2),life.Calls);Assert.True(life.Cleaned);
    }
    [Fact]
    public async Task Unexpected_kill_error_is_not_hidden_even_after_natural_exit()
    {
        var error=new IOException("unexpected stop failure");
        var life=new Lifetime{KillError=error};var cleanup=life.Create();
        Assert.Same(error,await Assert.ThrowsAsync<IOException>(()=>cleanup.DisposeAsync().AsTask()));
        Assert.Equal((1,0,0,0),life.Calls);Assert.False(life.Cleaned);
        await cleanup.DisposeAsync();
        Assert.Equal((1,0,1,1),life.Calls);Assert.True(life.Cleaned);
    }
}
