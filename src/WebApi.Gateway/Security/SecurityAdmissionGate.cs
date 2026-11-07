namespace WebApi.Gateway.Security;

// Authentication reads overlap. Waiting writers block new readers before draining
// existing admissions. The state lock never surrounds an asynchronous wait.
public sealed class SecurityAdmissionGate
{
    private readonly object sync = new();
    private TaskCompletionSource changed = NewSignal();
    private int readers;
    private int waitingWriters;
    private bool writer;

    public async Task<Ticket> EnterReadAsync(CancellationToken ct = default)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Task next;
            lock (sync)
            {
                if (!writer && waitingWriters == 0)
                {
                    readers++;
                    return new(this, writer: false);
                }
                next = changed.Task;
            }
            await next.WaitAsync(ct);
        }
    }

    public async Task<Ticket> EnterWriteAsync(CancellationToken ct = default)
    {
        lock (sync) { waitingWriters++; }
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                Task next;
                lock (sync)
                {
                    if (!writer && readers == 0)
                    {
                        waitingWriters--;
                        writer = true;
                        return new(this, writer: true);
                    }
                    next = changed.Task;
                }
                await next.WaitAsync(ct);
            }
        }
        catch
        {
            TaskCompletionSource wake;
            lock (sync) { waitingWriters--; wake = ReplaceSignal(); }
            wake.TrySetResult();
            throw;
        }
    }

    private void Exit(bool writing)
    {
        TaskCompletionSource? wake = null;
        lock (sync)
        {
            if (writing) writer = false;
            else readers--;
            if (writing || readers == 0) wake = ReplaceSignal();
        }
        wake?.TrySetResult();
    }

    // Called with sync held; complete the detached signal only after releasing sync.
    private TaskCompletionSource ReplaceSignal()
    {
        var previous = changed;
        changed = NewSignal();
        return previous;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public sealed class Ticket : IDisposable
    {
        private readonly SecurityAdmissionGate owner;
        private readonly bool writer;
        private int released;
        internal Ticket(SecurityAdmissionGate owner, bool writer) { this.owner = owner; this.writer = writer; }
        public void Dispose() { if (Interlocked.Exchange(ref released, 1) == 0) owner.Exit(writer); }
    }
}
