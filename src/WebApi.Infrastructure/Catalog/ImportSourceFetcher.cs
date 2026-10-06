using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.OpenApi;
using WebApi.Infrastructure.Contracts;
namespace WebApi.Infrastructure.Catalog;
public sealed class ImportSourceFetcher(ImportSourceSettings settings, IContractDnsResolver dns)
{
    public async Task<ContractSource> FetchAsync(Uri uri, ImportSourcePolicyDto policy, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var addressPolicy = new ImportAddressPolicy(settings);
        var allowances = addressPolicy.RequireUri(uri, policy);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(settings.NetworkTimeout);
        try {
            var host = uri.IdnHost.Trim('[', ']');
            var answers = IPAddress.TryParse(host, out var literal) ? [literal] : await dns.ResolveAsync(host, deadline.Token);
            var addresses = addressPolicy.RequireAddresses(uri, answers, allowances);
            using var handler = new SocketsHttpHandler {
                AllowAutoRedirect = false, UseProxy = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None,
                ConnectTimeout = settings.ConnectTimeout,
                ConnectCallback = async (context, token) => {
                    foreach (var address in addresses) {
                        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                        try { await socket.ConnectAsync(address, context.DnsEndPoint.Port, token); return new NetworkStream(socket, true); }
                        catch (OperationCanceledException) { socket.Dispose(); throw; }
                        catch (SocketException) { socket.Dispose(); }
                    }
                    throw FetchFailed();
                }
            };
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if ((int)response.StatusCode is >= 300 and < 400) throw ImportAddressPolicy.Rejected();
            if (!response.IsSuccessStatusCode) throw FetchFailed();
            var maximum = Math.Min(policy.Limits.MaxDocumentBytes, settings.Limits.MaxDocumentBytes);
            if (response.Content.Headers.ContentLength > maximum) throw TooLarge();
            await using var raw = await response.Content.ReadAsStreamAsync(deadline.Token);
            await using var bounded = new BoundedSourceStream(raw, maximum);
            var coding = response.Content.Headers.ContentEncoding.ToArray();
            if (coding.Length > 1) throw ImportAddressPolicy.Rejected();
            await using Stream decoded = coding.FirstOrDefault()?.ToLowerInvariant() switch {
                null or "identity" => bounded,
                "gzip" => new GZipStream(bounded, CompressionMode.Decompress, true),
                "deflate" => new DeflateStream(bounded, CompressionMode.Decompress, true),
                "br" => new BrotliStream(bounded, CompressionMode.Decompress, true),
                _ => throw ImportAddressPolicy.Rejected()
            };
            using var output = new MemoryStream(); var buffer = new byte[8192];
            while (true) { var read = await decoded.ReadAsync(buffer, deadline.Token); if (read == 0) break; if (output.Length + read > maximum) throw TooLarge(); output.Write(buffer, 0, read); }
            var text = new UTF8Encoding(false, true).GetString(output.ToArray());
            return new(uri, text, text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n').StartsWith('{') ? "json" : "yaml");
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new WebApi.Contracts.Common.ApiException(504, "import_source_timeout", "契约来源读取超时。"); }
        catch (DecoderFallbackException) { throw ImportAddressPolicy.Rejected(); }
        catch (Exception e) when (e is HttpRequestException or SocketException or IOException) { throw FetchFailed(); }
    }
    private static WebApi.Contracts.Common.ApiException FetchFailed() => new(502, "import_source_failed", "无法读取契约来源。");
    internal static WebApi.Contracts.Common.ApiException TooLarge() => new(413, "import_source_too_large", "契约来源原始或解压字节超过预算。");
    private sealed class BoundedSourceStream(Stream inner, int maximum) : Stream
    {
        private long read;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        private int Count(int n) { if ((read += n) > maximum) throw TooLarge(); return n; }
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => Count(await inner.ReadAsync(buffer, ct));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadArrayAsync(buffer, offset, count, ct);
        private async Task<int> ReadArrayAsync(byte[] b, int o, int n, CancellationToken ct) => await ReadAsync(b.AsMemory(o, n), ct);
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
