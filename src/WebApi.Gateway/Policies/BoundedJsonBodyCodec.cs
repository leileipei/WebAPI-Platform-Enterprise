using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using WebApi.Domain.Policies;
namespace WebApi.Gateway.Policies;
public sealed record JsonBodyReadResult(byte[]? Utf8,string? Reason,byte[]? Encoded=null);
public static class BoundedJsonBodyCodec
{
    public static async ValueTask<JsonBodyReadResult> ReadAsync(Stream source,string? contentEncoding,int maxBytes,CancellationToken ct)
    {
        if(maxBytes is <1024 or >1048576)throw new ArgumentOutOfRangeException(nameof(maxBytes));
        var encoding=contentEncoding?.Trim().ToLowerInvariant()??"identity";
        byte[]? encoded=null,decoded=null;
        try
        {
            // Enforce wire limit independently of whether the codec is supported.
            encoded=await ReadBoundedAsync(source,maxBytes,ct);
            if(encoding is not ("identity" or "gzip" or "br"))return Failure("transform_body_encoding_unsupported");
            if(encoding=="identity")decoded=encoded;
            else
            {
                using var input=new MemoryStream(encoded,false);
                await using Stream reader=encoding=="gzip"?new GZipStream(input,CompressionMode.Decompress,true):new BrotliStream(input,CompressionMode.Decompress,true);
                decoded=await ReadBoundedAsync(reader,maxBytes,ct);
            }
            if(encoding=="gzip"&&!await CompleteGzipAsync(encoded,decoded,ct))return Failure("transform_body_encoding_unsupported");
            if(encoding=="br")
            {
                using var decoder=new BrotliDecoder();ct.ThrowIfCancellationRequested();
                var status=decoder.Decompress(encoded,decoded,out var consumed,out var written);
                ct.ThrowIfCancellationRequested();
                if(status!=OperationStatus.Done||consumed!=encoded.Length||written!=decoded.Length)return Failure("transform_body_encoding_unsupported");
            }
            var invalid=JsonBodyMapper.ValidateUtf8(decoded,ct);
            if(invalid is not null)return Failure(invalid);
            return new(decoded,null,encoded);
        }
        catch(BodyLimitExceeded){return Failure("transform_body_too_large");}
        catch(Exception error)when(error is InvalidDataException or InvalidOperationException){return Failure("transform_body_encoding_unsupported");}
        catch{Clear();throw;}
        JsonBodyReadResult Failure(string reason){Clear();return new(null,reason);}
        void Clear(){if(decoded is not null)CryptographicOperations.ZeroMemory(decoded);if(encoded is not null&&!ReferenceEquals(encoded,decoded))CryptographicOperations.ZeroMemory(encoded);}
    }
    // Stream codecs may return a complete JSON prefix for truncated input. Validate the gzip
    // member boundaries, checksums and trailers without restricting valid concatenated members.
    private static async Task<bool> CompleteGzipAsync(byte[] encoded,byte[] decoded,CancellationToken ct)
    {
        using var source=new SingleByteReader(encoded);var buffer=ArrayPool<byte>.Shared.Rent(8192);var outputOffset=0;
        try
        {
            while(source.Offset<encoded.Length)
            {
                ct.ThrowIfCancellationRequested();var headerStart=source.Offset;var position=headerStart;
                if(encoded.Length-position<10||encoded[position]!=0x1f||encoded[position+1]!=0x8b||encoded[position+2]!=8||(encoded[position+3]&0xe0)!=0)return false;
                var flags=encoded[position+3];position+=10;
                if((flags&4)!=0)
                {if(encoded.Length-position<2)return false;var length=System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(encoded.AsSpan(position,2));position+=2;if(length>encoded.Length-position)return false;position+=length;}
                if((flags&8)!=0&&!SkipZero(encoded,ref position))return false;
                if((flags&16)!=0&&!SkipZero(encoded,ref position))return false;
                if((flags&2)!=0)
                {if(encoded.Length-position<2)return false;var checksum=System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(encoded.AsSpan(position,2));if(checksum!=(ushort)~Crc(0xffffffff,encoded.AsSpan(headerStart,position-headerStart)))return false;position+=2;}
                source.Offset=position;uint size=0,crc=0xffffffff;
                using(var deflate=new DeflateStream(source,CompressionMode.Decompress,true))
                {
                    while(true)
                    {
                        var count=await deflate.ReadAsync(buffer.AsMemory(0,buffer.Length),ct);if(count==0)break;
                        if(count>decoded.Length-outputOffset||!buffer.AsSpan(0,count).SequenceEqual(decoded.AsSpan(outputOffset,count)))return false;
                        outputOffset+=count;size+=(uint)count;crc=Crc(crc,buffer.AsSpan(0,count));
                    }
                }
                position=source.Offset;
                if(encoded.Length-position<8||System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(position,4))!=~crc
                    ||System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(position+4,4))!=size)return false;
                source.Offset=position+8;
            }
            return outputOffset==decoded.Length;
        }
        finally{ArrayPool<byte>.Shared.Return(buffer,true);}
    }
    private static bool SkipZero(byte[] bytes,ref int position)
    {while(position<bytes.Length)if(bytes[position++]==0)return true;return false;}
    private static uint Crc(uint crc,ReadOnlySpan<byte> bytes)
    {foreach(var value in bytes){crc^=value;for(var bit=0;bit<8;bit++)crc=(crc>>1)^((crc&1)==0?0:0xedb88320u);}return crc;}
    private sealed class SingleByteReader(byte[] bytes) : Stream
    {
        public int Offset {get;set;}
        public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;
        public override long Length=>bytes.Length;public override long Position{get=>Offset;set=>throw new NotSupportedException();}
        public override int Read(byte[] buffer,int offset,int count)=>Read(buffer.AsSpan(offset,count));
        public override int Read(Span<byte> buffer){if(buffer.Length==0||Offset>=bytes.Length)return 0;buffer[0]=bytes[Offset++];return 1;}
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default){ct.ThrowIfCancellationRequested();return ValueTask.FromResult(Read(buffer.Span));}
        public override void Flush(){}public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
        public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }
    private sealed class BodyLimitExceeded : Exception;
    private static async Task<byte[]> ReadBoundedAsync(Stream source,int maximum,CancellationToken ct)
    {
        using var output=new MemoryStream(Math.Min(maximum+1,8192));var buffer=ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while(true)
            {
                var count=await source.ReadAsync(buffer.AsMemory(0,Math.Min(buffer.Length,maximum+1-(int)output.Length)),ct);
                if(count==0)break;
                output.Write(buffer,0,count);if(output.Length>maximum)throw new BodyLimitExceeded();
            }
            ct.ThrowIfCancellationRequested();return output.ToArray();
        }
        finally{ArrayPool<byte>.Shared.Return(buffer,true);if(output.TryGetBuffer(out var retained))CryptographicOperations.ZeroMemory(retained.AsSpan());}
    }
}
