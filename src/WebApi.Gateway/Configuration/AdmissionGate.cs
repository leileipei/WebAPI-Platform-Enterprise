namespace WebApi.Gateway.Configuration;
public sealed class AdmissionGate
{
    public static readonly object Item=new();private readonly SemaphoreSlim gate=new(1,1);
    public async Task<Ticket> EnterAsync(CancellationToken ct) {await gate.WaitAsync(ct);return new(gate);}
    public sealed class Ticket(SemaphoreSlim gate) : IDisposable {private int released;public void Dispose() {if(Interlocked.Exchange(ref released,1)==0) gate.Release();}}
}
