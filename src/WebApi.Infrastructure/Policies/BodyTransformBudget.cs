namespace WebApi.Infrastructure.Policies;

public sealed class BodyTransformBudget
{
    private readonly int maximum;
    private int active;
    public BodyTransformBudget(int maxConcurrent = 16)
    {
        if (maxConcurrent is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(maxConcurrent));
        maximum = maxConcurrent;
    }
    public bool TryAcquire(out Lease? lease)
    {
        lease = null;
        while (true)
        {
            var count = Volatile.Read(ref active);
            if (count >= maximum) return false;
            if (Interlocked.CompareExchange(ref active, count + 1, count) != count) continue;
            lease = new(this); return true;
        }
    }
    public sealed class Lease : IDisposable
    {
        private BodyTransformBudget? owner;
        internal Lease(BodyTransformBudget owner) { this.owner = owner; }
        public void Dispose()
        { var held = Interlocked.Exchange(ref owner, null); if (held is not null) Interlocked.Decrement(ref held.active); }
    }
}
