using System.Security.Cryptography;

namespace WebApi.Migrator;

/// <summary>Hashes one non-seekable archive while enforcing its byte bound.</summary>
internal sealed class BoundedHashingReadStream(Stream inner, long maximum) : Stream
{
    private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long total;
    private bool eof;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    private int Observe(ReadOnlySpan<byte> bytes, int count)
    {
        if (count == 0) { if (!bytes.IsEmpty) eof = true; return 0; }
        total = checked(total + count);
        if (total > maximum) throw new InvalidOperationException("migration_evidence_archive_invalid");
        hash.AppendData(bytes[..count]);return count;
    }

    public byte[] FinishHash()
    {
        if (!eof || total < 1024 || total % 512 != 0) throw new InvalidOperationException("migration_evidence_archive_invalid");
        return hash.GetHashAndReset();
    }

    public override int Read(byte[] buffer, int offset, int count) => Observe(buffer.AsSpan(offset, count), inner.Read(buffer, offset, count));
    public override int Read(Span<byte> buffer) => Observe(buffer, inner.Read(buffer));
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var count = await inner.ReadAsync(buffer, cancellationToken);return Observe(buffer.Span, count);
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) hash.Dispose();base.Dispose(disposing); }
}
