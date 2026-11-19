using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WebApi.Contracts.Common;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.CredentialSecurity;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class GovernanceAuxiliaryProofHttpTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RotationProofSubmissionAndReadUseDistinctNonceBoundSignedExchanges(bool replay)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var hash = new string('a', 64);
        var proof = new FormalRotationRequestProof(id, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "GET", Guid.NewGuid(), 1, 1, hash, Guid.NewGuid(), 3, hash, Guid.NewGuid(), now, now, 200);
        using var authority = new Authority();
        byte[]? stored = null;
        using var transport = authority.Transport((path, bytes) =>
        {
            Assert.Equal("https://witness.example.test/proofs", path);
            using var document = JsonDocument.Parse(bytes);
            var action = document.RootElement.GetProperty("scope").GetProperty("action").GetString();
            if (action == "submit")
            {
                var request = authority.Read<FormalRotationProofSubmission>(bytes);
                Assert.Equal(proof, request.Scope.Proof);
                stored = authority.Reply(FormalRotationProofHttpWitness.Protocol, request, new FormalRotationProofStoreResult(true));
                return stored;
            }
            var observation = authority.Read<FormalRotationProofObservation>(bytes);
            Assert.Equal(proof.CredentialId, observation.Scope.Scope.ReplacementCredentialId);
            return replay ? stored! : authority.Reply(FormalRotationProofHttpWitness.Protocol, observation, new FormalRotationProofReadResult([proof]));
        });
        using var witness = new FormalRotationProofHttpWitness(transport);
        await witness.StoreAsync(proof, default);
        var scope = new FormalRotationRequestProofScope(authority.Journal, GovernanceJournalProtocol.Genesis, Guid.NewGuid(),
            proof.CredentialId, now, now.AddMinutes(1), new(Guid.NewGuid(), Guid.NewGuid(), id, proof.SecurityGeneration,
                proof.SecurityEpoch, hash, proof.ReleaseId, hash, 1, 1,
                [new(proof.NodeId, proof.InstanceId, proof.LeaseId, hash, hash, now.AddSeconds(30), now.AddSeconds(30))]));

        if (replay) await Assert.ThrowsAsync<InvalidDataException>(() => witness.ObserveAsync(scope, default));
        else Assert.Equal(proof, Assert.Single(await witness.ObserveAsync(scope, default)));
    }

    [Theory]
    [InlineData("held", true)]
    [InlineData("changed-scope", false)]
    [InlineData("replay", false)]
    [InlineData("release-unknown", true)]
    [InlineData("release-transport-failure", true)]
    [InlineData("release-absent", true)]
    public async Task LegacyHistoryRequiresFreshAttestationOfTheSameHeldWriterFence(string scenario, bool held)
    {
        var env = Guid.NewGuid();
        var hash = new string('b', 64);
        var evidence = new LegacyEnvironmentHistoryEvidence(new(env, Guid.NewGuid(), Guid.NewGuid(),
            new MigrationDatabaseIdentity("fixture", "fixture", 7), "20260101000000_Fixture", hash, hash, DateTimeOffset.UtcNow),
            hash, hash, hash, true, true, true, LegacyEnvironmentPublication.NeverPublished, []);
        using var authority = new Authority();
        var actions = new List<string>();
        var log = new LegacyLog();
        Guid? hold = null;
        byte[]? opening = null;
        using var transport = authority.Transport((_, bytes) =>
        {
            var request = authority.Read<LegacyEnvironmentHistoryCommand>(bytes);
            var scope = request.Scope;
            Assert.Equal(env, scope.EnvironmentId);
            hold ??= scope.HoldId;
            Assert.Equal(hold, scope.HoldId);
            actions.Add(scope.Action);
            if (scope.Action == "check" && scenario == "replay") return opening!;
            var digest = LegacyEnvironmentHistoryHttpReader.EvidenceHash(evidence);
            if (scope.Action == "check" && scenario == "changed-scope") digest = hash;
            if (scope.Action == "release" && scenario == "release-transport-failure") throw new HttpRequestException();
            var state = scope.Action == "release" ? scenario switch
            {
                "release-unknown" => "Held",
                "release-absent" => "Absent",
                _ => "Released"
            } : "Held";
            var result = new LegacyEnvironmentHistoryObservation(env, scope.HoldId, state, digest, scope.Action == "open" ? evidence : null);
            var reply = authority.Reply(LegacyEnvironmentHistoryHttpReader.Protocol, request, result);
            if (scope.Action == "open") opening = reply;
            return reply;
        });
        var reader = new LegacyEnvironmentHistoryHttpReader(() => transport, log);
        await using (var fence = await reader.OpenAsync(env, default))
        {
            Assert.NotNull(fence);
            Assert.Equal(evidence.Source, fence.Evidence.Source);
            Assert.Equal(held, await fence.IsHeldAsync(default));
            Assert.Equal(new[] { "open", "check" }, actions);
        }
        Assert.Equal(new[] { "open", "check", "release" }, actions);
        if (scenario is "release-unknown" or "release-transport-failure")
        {
            var warning = Assert.Single(log.Warnings);
            Assert.Contains(env.ToString(), warning);
            Assert.Contains(hold!.Value.ToString(), warning);
        }
        else Assert.Empty(log.Warnings);
    }

    private sealed class LegacyLog : ILogger<LegacyEnvironmentHistoryHttpReader>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Warning;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> format)
        {
            if (IsEnabled(logLevel)) Warnings.Add(format(state, exception));
        }
    }

    private sealed class Authority : IDisposable
    {
        private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public GovernanceJournalId Journal { get; } = new(Guid.NewGuid(), Guid.NewGuid());

        public GovernanceProofHttpTransport Transport(Func<string, byte[], byte[]> reply) =>
            new(new Uri("https://witness.example.test/proofs"),
                new("fixture-source", "fixture-key", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())),
                Journal, new BoundedWitnessHttpClient(new SyntheticHttp(reply), TimeSpan.FromSeconds(1)));

        public GovernanceEvidenceExchangeRequest<T> Read<T>(byte[] bytes)
        {
            var request = JsonSerializer.Deserialize<GovernanceEvidenceExchangeRequest<T>>(bytes, CanonicalJson.Options)!;
            Assert.Equal(Journal, request.Journal);
            return request;
        }

        public byte[] Reply<TRequest, TResponse>(string protocol, GovernanceEvidenceExchangeRequest<TRequest> request, TResponse result)
        {
            var payload = new GovernanceEvidenceExchangePayload<TResponse>(
                GovernanceEvidenceExchangeProtocol.RequestHash(protocol, request), "fixture-source", "fixture-key", result);
            var signature = key.SignData(SecuritySignedEncoding.Unsigned(GovernanceEvidenceExchangeProtocol.ResponseDomain(protocol), payload),
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return CanonicalJson.Serialize(new SignedGovernanceEvidenceExchangeResponse<TResponse>(payload, Convert.ToBase64String(signature)));
        }

        public void Dispose() => key.Dispose();
    }

    private sealed class SyntheticHttp(Func<string, byte[], byte[]> reply) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var bytes = await request.Content!.ReadAsByteArrayAsync(ct);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(reply(request.RequestUri!.AbsoluteUri, bytes))
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return response;
        }
    }
}
