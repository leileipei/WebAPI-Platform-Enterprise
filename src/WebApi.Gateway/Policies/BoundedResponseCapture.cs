using System.IO.Pipelines;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Yarp.ReverseProxy.Forwarder;
namespace WebApi.Gateway.Policies;
public static class BoundedResponseCapture
{
    public static CaptureLease Install(HttpContext context,int maxEntryBytes)=>new(context,maxEntryBytes);
    public sealed class CaptureLease : IHttpResponseBodyFeature,IDisposable
    {
        private readonly HttpContext context;
        private readonly IHttpResponseBodyFeature original;
        private readonly int budget;
        private byte[]? buffer;
        private int retained;
        private long written;
        private bool invalid,disposed;
        private DateTimeOffset? responseAt;
        internal CaptureLease(HttpContext context,int maxEntryBytes)
        {
            if(maxEntryBytes is <1 or >1048576)throw new ArgumentOutOfRangeException(nameof(maxEntryBytes));
            this.context=context;budget=maxEntryBytes;original=context.Features.Get<IHttpResponseBodyFeature>()??throw new InvalidOperationException("Response body feature is unavailable.");
            Stream=new CaptureStream(original.Stream,this);Writer=new CaptureWriter(original.Writer,this);
            context.Features.Set<IHttpResponseBodyFeature>(this);
            context.Response.OnStarting(()=>{responseAt=DateTimeOffset.UtcNow;return Task.CompletedTask;});
        }
        public Stream Stream {get;}
        public PipeWriter Writer {get;}
        public DateTimeOffset ResponseAt=>responseAt??DateTimeOffset.UtcNow;
        private void Retain(ReadOnlySpan<byte> bytes)
        {
            written+=bytes.Length;
            if(invalid)return;
            if(bytes.Length>budget-retained){invalid=true;buffer=null;return;}
            if(bytes.Length==0)return;
            buffer??=new byte[budget];bytes.CopyTo(buffer.AsSpan(retained));retained+=bytes.Length;
        }
        public CacheEntry? TryComplete(CacheStorageRule rule)
        {
            if(disposed||invalid||!rule.Store||context.RequestAborted.IsCancellationRequested||context.Features.Get<IForwarderErrorFeature>() is not null
                ||context.Response.ContentLength is long expected&&expected!=written||context.Features.Get<IHttpResponseTrailersFeature>()?.Trailers.Count>0)return null;
            var headers=new Dictionary<string,string[]>(StringComparer.OrdinalIgnoreCase);long bytes=retained;
            foreach(var header in context.Response.Headers)
            {
                if(!CacheEligibility.StoredHeaderNames.Contains(header.Key))continue;
                if(header.Value.Count>8)return null;
                var values=header.Value.Select(v=>v??"").ToArray();
                foreach(var value in values){bytes+=Encoding.UTF8.GetByteCount(header.Key)+Encoding.UTF8.GetByteCount(value);if(bytes>budget)return null;}
                headers.Add(header.Key,values);
            }
            return new(headers,buffer is null?[]:buffer.AsSpan(0,retained).ToArray(),ResponseAt,rule.InitialAge,rule.FreshFor);
        }
        public void DisableBuffering()=>original.DisableBuffering();
        public Task StartAsync(CancellationToken ct=default)=>original.StartAsync(ct);
        public Task CompleteAsync()=>original.CompleteAsync();
        public Task SendFileAsync(string path,long offset,long? count,CancellationToken ct=default){invalid=true;buffer=null;return original.SendFileAsync(path,offset,count,ct);}
        public void Dispose(){if(disposed)return;disposed=true;buffer=null;context.Features.Set(original);}
        private sealed class CaptureStream(Stream inner,CaptureLease owner):Stream
        {
            public override bool CanRead=>false;public override bool CanSeek=>false;public override bool CanWrite=>inner.CanWrite;
            public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
            public override void Flush()=>inner.Flush();public override Task FlushAsync(CancellationToken ct)=>inner.FlushAsync(ct);
            public override void Write(byte[] bytes,int offset,int count){try{inner.Write(bytes,offset,count);owner.Retain(bytes.AsSpan(offset,count));}catch{owner.invalid=true;throw;}}
            public override void Write(ReadOnlySpan<byte> bytes){try{inner.Write(bytes);owner.Retain(bytes);}catch{owner.invalid=true;throw;}}
            public override Task WriteAsync(byte[] bytes,int offset,int count,CancellationToken ct)=>WriteAsync(bytes.AsMemory(offset,count),ct).AsTask();
            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes,CancellationToken ct=default){try{await inner.WriteAsync(bytes,ct);owner.Retain(bytes.Span);}catch{owner.invalid=true;throw;}}
            public override int Read(byte[] bytes,int offset,int count)=>throw new NotSupportedException();public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();
        }
        private sealed class CaptureWriter(PipeWriter inner,CaptureLease owner):PipeWriter
        {
            private Memory<byte> pending;
            public override bool CanGetUnflushedBytes=>inner.CanGetUnflushedBytes;
            public override long UnflushedBytes=>inner.UnflushedBytes;
            public override Memory<byte> GetMemory(int sizeHint=0)=>pending=inner.GetMemory(sizeHint);
            public override Span<byte> GetSpan(int sizeHint=0)=>GetMemory(sizeHint).Span;
            public override void Advance(int count){try{owner.Retain(pending.Span[..count]);inner.Advance(count);pending=default;}catch{owner.invalid=true;throw;}}
            public override void CancelPendingFlush()=>inner.CancelPendingFlush();
            public override void Complete(Exception? exception=null){if(exception is not null)owner.invalid=true;inner.Complete(exception);}
            public override async ValueTask<FlushResult> FlushAsync(CancellationToken ct=default){try{var result=await inner.FlushAsync(ct);if(result.IsCanceled)owner.invalid=true;return result;}catch{owner.invalid=true;throw;}}
        }
    }
}
