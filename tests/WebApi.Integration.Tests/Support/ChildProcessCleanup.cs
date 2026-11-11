using System.ComponentModel;

namespace WebApi.Integration.Tests.Support;

internal sealed class ChildProcessCleanup(
    Func<bool> hasExited,Action kill,Func<Task> waitForExit,Action release,Action cleanup) : IAsyncDisposable
{
    private bool disposed;
    private bool processReleased;
    public async ValueTask DisposeAsync()
    {
        if(disposed)return;
        if(!processReleased)
        {
            if(!hasExited())
            {
                try{kill();}
                catch(Exception error) when(error is InvalidOperationException or Win32Exception && hasExited()) { }
                await waitForExit();
            }
            if(!hasExited())throw new InvalidOperationException("Isolated control-plane process did not exit.");
            release();
            processReleased=true;
        }
        cleanup();
        disposed=true;
    }
}
