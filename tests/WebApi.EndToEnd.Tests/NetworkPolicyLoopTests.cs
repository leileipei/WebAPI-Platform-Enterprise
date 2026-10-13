using System.Security.Cryptography;
using System.Text.Json;
using Xunit;
namespace WebApi.EndToEnd.Tests;
public sealed class NetworkPolicyLoopTests
{
    [Fact, Trait("Scenario", "NetworkPolicies")]
    public async Task FixedImageIndependentNetworkPolicyEvidenceIsComplete()
    {
        var directory = System.Environment.GetEnvironmentVariable("WEBAPI_NETWORK_POLICY_RESULT_DIRECTORY");
        Assert.False(string.IsNullOrWhiteSpace(directory), "Missing evidence must fail, never skip.");
        var proofBytes = await File.ReadAllBytesAsync(Path.Combine(directory!, "acceptance-input.json"));
        using var proofDocument = JsonDocument.Parse(proofBytes);
        using var resultDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(directory!, "acceptance-result.json")));
        var proof = proofDocument.RootElement; var result = resultDocument.RootElement;
        Assert.True(result.GetProperty("passed").GetBoolean());
        Assert.Equal("isolated-pre-install", result.GetProperty("stage").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(proofBytes)), result.GetProperty("proofSha256").GetString());
        foreach (var key in new[] { "sourceRevision", "imageId", "ownerId" }) Assert.Equal(proof.GetProperty(key).GetString(), result.GetProperty(key).GetString());
        Assert.False(proof.GetProperty("runtime").GetProperty("restored").GetBoolean());
        Assert.True(proof.GetProperty("runtime").GetProperty("initializedEmpty").GetBoolean());
        Assert.Equal(7, result.GetProperty("actualContainers").GetArrayLength());
        Assert.NotEmpty(result.GetProperty("staticHashes").EnumerateObject());
        var ids = Enumerable.Range(1, 12).Select(i => $"P3-{i:00}").ToArray();
        Assert.Equal(ids, result.GetProperty("sections").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(ids, proof.GetProperty("sections").EnumerateArray().Select(x => x.GetProperty("id").GetString()).Order());
        foreach (var artifact in proof.GetProperty("artifacts").EnumerateArray())
        {
            var name = artifact.GetProperty("path").GetString()!;
            Assert.False(Path.IsPathRooted(name)); Assert.DoesNotContain("..", name.Split('/')); Assert.DoesNotContain("", name.Split('/'));
            var bytes = await File.ReadAllBytesAsync(Path.Combine(directory!, "public", name));
            Assert.Equal(artifact.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        foreach (var section in proof.GetProperty("sections").EnumerateArray())
        {
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(directory!, "public", section.GetProperty("artifact").GetString()!)));
            Assert.True(document.RootElement.GetProperty("actual").GetBoolean());
            foreach (var key in new[] { "sourceRevision", "imageId", "ownerId" }) Assert.Equal(proof.GetProperty(key).GetString(), document.RootElement.GetProperty(key).GetString());
        }
    }
}
