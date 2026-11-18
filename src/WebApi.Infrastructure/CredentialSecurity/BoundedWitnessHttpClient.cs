using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;

namespace WebApi.Infrastructure.CredentialSecurity;

internal sealed class BoundedWitnessHttpClient : IDisposable
{
    private readonly HttpClient client;
    private readonly TimeSpan timeout;

    public BoundedWitnessHttpClient(X509Certificate2? certificate = null) : this(CreateHandler(certificate), TimeSpan.FromSeconds(5)) { }

    private static SocketsHttpHandler CreateHandler(X509Certificate2? certificate)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(3),
            MaxConnectionsPerServer = 8,
            MaxResponseHeadersLength = 16,
            MaxResponseDrainSize = 0,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        if (certificate is not null) handler.SslOptions.ClientCertificates = new X509CertificateCollection { certificate };
        return handler;
    }

    internal BoundedWitnessHttpClient(HttpMessageHandler handler, TimeSpan timeout)
    {
        client = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
        this.timeout = timeout;
    }

    internal static Uri RequireEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(endpoint.Host) ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new ArgumentException("The witness requires a fixed HTTPS endpoint.", nameof(endpoint));
        return endpoint;
    }

    internal async Task<byte[]> PostAsync(Uri endpoint, byte[] payload, int maximumBytes, CancellationToken ct)
    {
        if (payload.Length < 2 || payload.Length > maximumBytes) throw Unavailable();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(timeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(payload) };
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token);
            var headers = response.Content.Headers;
            var charset = headers.ContentType?.CharSet;
            if (charset is ['"', .., '"']) charset = charset[1..^1];
            var declaredLength = headers.ContentLength;
            if (response.StatusCode != HttpStatusCode.OK ||
                !string.Equals(headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
                charset is not null && !string.Equals(charset, "utf-8", StringComparison.OrdinalIgnoreCase) ||
                headers.ContentEncoding.Count != 0 || declaredLength < 2 || declaredLength > maximumBytes)
                throw Unavailable();

            await using var body = await response.Content.ReadAsStreamAsync(budget.Token);
            using var result = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var remaining = maximumBytes + 1 - (int)result.Length;
                var count = await body.ReadAsync(buffer.AsMemory(0, Math.Min(remaining, buffer.Length)), budget.Token);
                if (count == 0) break;
                if (count >= remaining) throw Unavailable();
                result.Write(buffer, 0, count);
            }
            budget.Token.ThrowIfCancellationRequested();
            if (result.Length < 2 || declaredLength is long length && result.Length != length) throw Unavailable();
            return result.ToArray();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw Unavailable(); }
        catch (Exception error) when (error is HttpRequestException or IOException) { throw Unavailable(); }
    }

    private static InvalidDataException Unavailable() => new("independent_security_evidence_unavailable");

    public void Dispose() => client.Dispose();
}
