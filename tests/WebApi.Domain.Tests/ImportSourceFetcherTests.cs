using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Common;
using WebApi.Contracts.OpenApi;
using WebApi.Infrastructure.Catalog;
using WebApi.Infrastructure.Contracts;
using Xunit;
namespace WebApi.Domain.Tests;

[CollectionDefinition("ImportSourceFetcherGlobal", DisableParallelization = true)]
public sealed class ImportSourceFetcherGlobalCollection;
[Collection("ImportSourceFetcherGlobal")]
public sealed class ImportSourceFetcherTests
{
    private sealed class Resolver(params IPAddress[][] answers) : IContractDnsResolver
    {
        public int Calls { get; private set; }
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Task.FromResult(answers[Math.Min(Calls++, answers.Length - 1)]);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource lifetime = new();
        private readonly Task serve;
        public Uri Origin { get; }
        public int Calls { get; private set; }
        public string? Host { get; private set; }
        public Fixture(byte[] body, string headers = "", int status = 200, int delayMs = 0)
        {
            listener.Start(); Origin = new($"http://contracts.test:{((IPEndPoint)listener.LocalEndpoint).Port}");
            serve = Task.Run(async () => {
                try {
                    using var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                    await using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    while (await reader.ReadLineAsync(lifetime.Token) is { Length: > 0 } line) if (line.StartsWith("Host: ")) Host = line[6..];
                    Calls++; if (delayMs > 0) await Task.Delay(delayMs, lifetime.Token);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Fixture\r\nContent-Length: {body.Length}\r\nConnection: close\r\n{headers}\r\n"), lifetime.Token);
                    await stream.WriteAsync(body, lifetime.Token);
                } catch (Exception e) when (e is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
            });
        }
        public async ValueTask DisposeAsync() { lifetime.Cancel(); listener.Stop(); await serve; lifetime.Dispose(); }
    }
    private static ImportSourcePolicyDto Policy(string origin, ContractLimits? limits = null) => new(Guid.NewGuid(), 1, [new(origin, "/contracts", ["127.0.0.1/32"])], limits ?? new());
    private static ImportSourceSettings FixtureSettings(string origin, TimeSpan? timeout = null) => new() { AllowHttp = true, AllowedPrivateCidrs = ["127.0.0.1/32"], FixtureOrigins = [origin], NetworkTimeout = timeout ?? TimeSpan.FromSeconds(15) };
    [Fact] public async Task AllowedFixtureUsesPinnedIpAndPreservesOriginalHost()
    {
        await using var fixture = new Fixture(Encoding.UTF8.GetBytes(ContractDocumentReaderTests.Yaml));
        var resolver = new Resolver([IPAddress.Loopback], [IPAddress.Parse("169.254.169.254")]);
        var source = await new ImportSourceFetcher(FixtureSettings(fixture.Origin.GetLeftPart(UriPartial.Authority)), resolver).FetchAsync(new(fixture.Origin, "/contracts/openapi.yaml"), Policy(fixture.Origin.GetLeftPart(UriPartial.Authority)), default);
        Assert.Equal(ContractDocumentReaderTests.Yaml, source.RawText);
        Assert.Equal("yaml", source.Format); Assert.Equal(1, resolver.Calls); Assert.Equal(1, fixture.Calls);
        Assert.Equal("contracts.test:" + fixture.Origin.Port, fixture.Host);
    }
    [Theory]
    [InlineData("/contracts-evil/a.yaml")]
    [InlineData("/contracts/%2f../a.yaml")]
    [InlineData("/contracts/%252e%252e/a.yaml")]
    [InlineData("/contracts/../other/a.yaml")]
    [InlineData("/contracts/./a.yaml")]
    [InlineData("/contracts\\other\\a.yaml")]
    [InlineData("/contracts/a.yaml?password=x")]
    [InlineData("/contracts/a.yaml#root")]
    public async Task EncodedPathCannotEscapeAllowance(string path)
    {
        var resolver = new Resolver([IPAddress.Loopback]);
        var fetcher = new ImportSourceFetcher(FixtureSettings("http://contracts.test:48123"), resolver);
        Assert.Equal("import_source_rejected", (await Assert.ThrowsAsync<ApiException>(() => fetcher.FetchAsync(new("http://contracts.test:48123" + path), Policy("http://contracts.test:48123"), default))).Code);
        Assert.Equal(0, resolver.Calls);
    }
    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("192.0.2.1")]
    public async Task MixedDnsRejectsEveryUnsafeAddressBeforeConnecting(string unsafeAddress)
    {
        var resolver = new Resolver([IPAddress.Parse("8.8.8.8"), IPAddress.Parse(unsafeAddress)]);
        var fetcher = new ImportSourceFetcher(new(), resolver);
        await Assert.ThrowsAsync<ApiException>(() => fetcher.FetchAsync(new("https://contracts.example/contracts/api.yaml"), new(Guid.NewGuid(), 1, [new("https://contracts.example", "/contracts", [])], new()), default));
        Assert.Equal(1, resolver.Calls);
    }
    [Fact] public async Task HttpPrivateAddressRequiresBothDeploymentAndProjectRules()
    {
        var resolver = new Resolver([IPAddress.Loopback]);
        await Assert.ThrowsAsync<ApiException>(() => new ImportSourceFetcher(new(), resolver).FetchAsync(new("http://contracts.test:48123/contracts/a.yaml"), Policy("http://contracts.test:48123"), default));
        Assert.Equal(0, resolver.Calls);
        var restricted = Policy("http://contracts.test:48123") with { Allowances = [new("http://contracts.test:48123", "/contracts", [])] };
        await Assert.ThrowsAsync<ApiException>(() => new ImportSourceFetcher(FixtureSettings("http://contracts.test:48123"), resolver).FetchAsync(new("http://contracts.test:48123/contracts/a.yaml"), restricted, default));
    }
    [Fact] public async Task RedirectAndOversizedCompressedBodyAreRejected()
    {
        await using (var fixture = new Fixture([], "Location: http://127.0.0.1:4192/\r\n", 302)) {
            var origin = fixture.Origin.GetLeftPart(UriPartial.Authority); var resolver = new Resolver([IPAddress.Loopback]);
            await Assert.ThrowsAsync<ApiException>(() => new ImportSourceFetcher(FixtureSettings(origin), resolver).FetchAsync(new(fixture.Origin, "/contracts/a.yaml"), Policy(origin), default));
            Assert.Equal(1, resolver.Calls); Assert.Equal(1, fixture.Calls);
        }
        using var output = new MemoryStream();
        using (var zip = new GZipStream(output, CompressionMode.Compress, true)) zip.Write(Encoding.UTF8.GetBytes(new string('x', 10000)));
        await using (var fixture = new Fixture(output.ToArray(), "Content-Encoding: gzip\r\n")) {
            var origin = fixture.Origin.GetLeftPart(UriPartial.Authority);
            Assert.Equal("import_source_too_large", (await Assert.ThrowsAsync<ApiException>(() => new ImportSourceFetcher(FixtureSettings(origin), new Resolver([IPAddress.Loopback])).FetchAsync(new(fixture.Origin, "/contracts/a.yaml"), Policy(origin, new(MaxDocumentBytes: 1000)), default))).Code);
        }
    }
    [Fact] public async Task SlowResponseExpiresAndCancellationIsNotSuccessful()
    {
        await using var fixture = new Fixture([1], delayMs: 300);
        var origin = fixture.Origin.GetLeftPart(UriPartial.Authority);
        var fetcher = new ImportSourceFetcher(FixtureSettings(origin, TimeSpan.FromMilliseconds(100)), new Resolver([IPAddress.Loopback]));
        var timedOut = await Assert.ThrowsAsync<ApiException>(() => fetcher.FetchAsync(new(fixture.Origin, "/contracts/a.yaml"), Policy(origin), default));
        Assert.Equal("import_source_timeout", timedOut.Code); Assert.Equal(504, timedOut.Status);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetcher.FetchAsync(new(fixture.Origin, "/contracts/a.yaml"), Policy(origin), new CancellationToken(true)));
    }
    [Fact] public async Task FailedUpstreamResponseUsesFetchFailureStatus()
    {
        await using var fixture = new Fixture([], status: 503);var origin=fixture.Origin.GetLeftPart(UriPartial.Authority);
        var failed=await Assert.ThrowsAsync<ApiException>(() => new ImportSourceFetcher(FixtureSettings(origin),new Resolver([IPAddress.Loopback])).FetchAsync(new(fixture.Origin,"/contracts/api.yaml"),Policy(origin),default));
        Assert.Equal("import_source_failed",failed.Code);Assert.Equal(502,failed.Status);Assert.Equal(1,fixture.Calls);
    }
    [Fact] public async Task ManagementOriginsAndUrlCredentialsNeverReachDns()
    {
        var resolver = new Resolver([IPAddress.Loopback]);
        var settings = FixtureSettings("http://contracts.test:48123") with { DeniedOrigins = ["http://contracts.test:48123"] };
        await Assert.ThrowsAsync<ApiException>(() => new ImportSourceFetcher(settings, resolver).FetchAsync(new("http://contracts.test:48123/contracts/api.yaml"), Policy("http://contracts.test:48123"), default));
        await Assert.ThrowsAsync<ApiException>(() => new ImportSourceFetcher(settings, resolver).FetchAsync(new("http://user:password@contracts.test:48123/contracts/api.yaml"), Policy("http://contracts.test:48123"), default));
        Assert.Equal(0, resolver.Calls);
    }

    [Fact] public async Task DefaultHttpProxyCannotReceiveTheContractRequest()
    {
        await using var fixture = new Fixture(Encoding.UTF8.GetBytes(ContractDocumentReaderTests.Yaml));
        await using var proxy = new Fixture(Encoding.UTF8.GetBytes("proxy-body"));
        var saved = HttpClient.DefaultProxy;
        try {
            HttpClient.DefaultProxy = new WebProxy(new Uri($"http://127.0.0.1:{proxy.Origin.Port}"));
            var origin = fixture.Origin.GetLeftPart(UriPartial.Authority);
            var source = await new ImportSourceFetcher(FixtureSettings(origin), new Resolver([IPAddress.Loopback])).FetchAsync(new(fixture.Origin, "/contracts/api.yaml"), Policy(origin), default);
            Assert.Equal(ContractDocumentReaderTests.Yaml, source.RawText); Assert.Equal(0, proxy.Calls);
        } finally { HttpClient.DefaultProxy = saved; }
    }

    [Fact] public void Ipv6AndMappedIpv4UseTheSamePrivateAndSpecialAddressRules()
    {
        var allowance = new ImportSourceAllowance("https://contracts.example", "/contracts", ["10.0.0.0/8", "fc00::/7"]);
        var policy = new ImportAddressPolicy(new() { AllowedPrivateCidrs = ["10.0.0.0/8", "fc00::/7"] });
        var result = policy.RequireAddresses(new("https://contracts.example/contracts/api.yaml"), [IPAddress.Parse("::ffff:10.0.0.1"), IPAddress.Parse("fd00::1"), IPAddress.Parse("2606:4700:4700::1111")], [allowance]);
        Assert.Equal(IPAddress.Parse("10.0.0.1"), result[0]); Assert.Equal(3, result.Count);
        Assert.Throws<ApiException>(() => policy.RequireAddresses(new("https://contracts.example/contracts/api.yaml"), [IPAddress.Parse("64:ff9b::a00:1")], [allowance]));
        Assert.Throws<ApiException>(() => policy.RequireAddresses(new("https://contracts.example/contracts/api.yaml"), [IPAddress.Parse("2001:db8::1")], [allowance]));
    }

    [Fact] public void SourceRuleStringsAreBoundedBeforePersistenceOrDns()
    {
        var policy = new ImportAddressPolicy(new());
        Assert.Throws<ApiException>(() => policy.ValidateAllowance(new("https://contracts.example", "/" + new string('x', 3000), [])));
        Assert.Throws<ApiException>(() => policy.RequireUri(new("https://contracts.example/contracts/" + new string('x', 5000)), new(Guid.NewGuid(), 1, [new("https://contracts.example", "/contracts", [])], new())));
    }
}
