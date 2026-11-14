using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace WebApi.Gateway.Tests.Support;

// Holds only this test's Redis handshake; the shared test Redis keeps running.
internal sealed class GatedRedisConnection : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentBag<Task> connections = [];
    private readonly Task accepting;

    internal GatedRedisConnection()
    {
        listener.Start();
        accepting = AcceptAsync();
    }

    internal string ConnectionString => $"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port},abortConnect=false,connectTimeout=5000,asyncTimeout=1000,connectRetry=0";
    internal void Release() => released.TrySetResult();

    private async Task AcceptAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                connections.Add(ForwardAsync(client));
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (SocketException) when (stop.IsCancellationRequested) { }
    }

    private async Task ForwardAsync(TcpClient client)
    {
        using (client)
        using (var upstream = new TcpClient())
        using (var closed = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            try
            {
                await upstream.ConnectAsync("redis", 6379, stop.Token);
                await released.Task.WaitAsync(stop.Token);
                var send = client.GetStream().CopyToAsync(upstream.GetStream(), closed.Token);
                var receive = upstream.GetStream().CopyToAsync(client.GetStream(), closed.Token);
                await Task.WhenAny(send, receive);
                closed.Cancel();
                await Task.WhenAll(send, receive);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested || closed.IsCancellationRequested) { }
            catch (IOException) { /* Either Redis or the test client closed its connection. */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        listener.Stop();
        try
        {
            await accepting.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(connections).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { stop.Dispose(); }
    }
}
