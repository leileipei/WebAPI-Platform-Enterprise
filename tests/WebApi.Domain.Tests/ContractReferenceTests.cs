using System.Text.Json;
using System.Text.Json.Nodes;
using WebApi.Contracts.Common;
using WebApi.Contracts.OpenApi;
using WebApi.Infrastructure.Contracts;
using Xunit;
namespace WebApi.Domain.Tests;

public sealed class ContractReferenceTests
{
    internal static readonly Uri Origin = new("https://contracts.example/openapi.json");
    internal static ContractDocument Document(string schemas) => new ContractDocumentReader().Read(
        new(Origin, "{\"openapi\":\"3.1.0\",\"info\":{\"title\":\"t\",\"version\":\"1\"},\"paths\":{},\"components\":{\"schemas\":" + schemas + "}}", "json"), new(), default);
    internal static ContractBundle Bundle(string schemas) => ContractBundleCodec.Create(Origin, [Document(schemas)]);

    [Fact] public void RecursiveRefUsesGraphIdentity()
    {
        var registry = new ContractReferenceRegistry(Bundle("{\"Node\":{\"type\":\"object\",\"properties\":{\"next\":{\"$ref\":\"#/components/schemas/Node\"}}}}"), new());
        var node = registry.Resolve(Origin, "#/components/schemas/Node");
        var next = registry.Resolve(node.ResourceUri, node.Node["properties"]!["next"]!["$ref"]!.GetValue<string>());
        Assert.Same(node.Node, next.Node);
        Assert.Equal("/components/schemas/Node", next.Pointer);
    }

    [Fact] public void BaseIdAnchorAndDynamicReferencePreserveScope()
    {
        var bundle = Bundle("{\"Node\":{\"$id\":\"models/node\",\"$dynamicAnchor\":\"node\",\"$defs\":{\"value\":{\"$anchor\":\"value\",\"type\":\"string\"}},\"properties\":{\"child\":{\"$dynamicRef\":\"#node\"}}}}");
        var registry = new ContractReferenceRegistry(bundle, new());
        var id = new Uri(Origin, "models/node");
        Assert.Equal("string", registry.Resolve(id, "#value").Node["type"]!.GetValue<string>());
        var node = registry.Resolve(id, "#node");
        Assert.Equal(id, node.ResourceUri);
        Assert.Same(node.Node, registry.Resolve(Origin, "models/node").Node);
        Assert.Equal("/components/schemas/Node", registry.Resolve(Origin, "#/components/schemas/Node").Pointer);
        Assert.Equal("#node", node.Node["properties"]!["child"]!["$dynamicRef"]!.GetValue<string>());
    }

    [Fact] public void MissingRefNeverBecomesEmptySchema()
    {
        var registry = new ContractReferenceRegistry(Bundle("{}"), new());
        Assert.Equal("missing_contract_reference", Assert.Throws<ApiException>(() => registry.Resolve(Origin, "missing.json#/x")).Code);
        Assert.Equal("missing_contract_reference", Assert.Throws<ApiException>(() => registry.Resolve(Origin, "#/no")).Code);
    }

    [Fact] public void DuplicateLogicalUrisAreRejected()
    {
        var doc = Document("{}");
        Assert.Throws<ApiException>(() => ContractBundleCodec.Create(Origin, [doc, doc]));
        Assert.Throws<ApiException>(() => new ContractReferenceRegistry(Bundle("{\"a\":{\"$id\":\"same\"},\"b\":{\"$id\":\"same\"}}"), new()));
        Assert.Throws<ApiException>(() => new ContractReferenceRegistry(Bundle("{\"a\":{\"$anchor\":\"same\"},\"b\":{\"$anchor\":\"same\"}}"), new()));
    }

    [Fact] public async Task SameIdInTwoBundlesDoesNotShareRegistrations()
    {
        async Task<string> Resolve(string type) => await Task.Run(() => {
            var registry = new ContractReferenceRegistry(Bundle("{\"item\":{\"$id\":\"shared\",\"type\":\"" + type + "\"}}"), new());
            return registry.Resolve(Origin, "shared").Node["type"]!.GetValue<string>();
        });
        var results = await Task.WhenAll(Resolve("integer"), Resolve("string"));
        Assert.Equal(["integer", "string"], results);
    }

    [Fact] public void FixedExternalBooleanSchemaResolvesWithoutNetwork()
    {
        var source = new ContractSource(new Uri(Origin, "allow.json"), "false", "json");
        var external = new ContractDocumentReader().ReadResource(source, new(), ContractDialect.Oas31, default);
        var bundle = ContractBundleCodec.Create(Origin, [Document("{\"external\":{\"$ref\":\"allow.json\"}}"), external]);
        Assert.False(new ContractReferenceRegistry(bundle, new()).Resolve(Origin, "allow.json").Node.GetValue<bool>());
    }

    [Fact] public void BundleRoundTripKeepsHashAndTamperingIsRejected()
    {
        var bundle = Bundle("{\"a\":{\"type\":\"string\"}}");
        var roundtrip = JsonSerializer.Deserialize<ContractBundle>(JsonSerializer.Serialize(bundle))!;
        ContractBundleCodec.Verify(roundtrip);
        Assert.Equal(bundle.Hash, ContractBundleCodec.Create(roundtrip.RootUri, roundtrip.Documents).Hash);
        roundtrip.Documents[0].Root["x-tampered"] = true;
        Assert.Throws<ApiException>(() => ContractBundleCodec.Verify(roundtrip));
        Assert.Throws<ApiException>(() => ContractBundleCodec.Verify(bundle with { Hash = new string('0', 64) }));
    }

    [Fact] public void BundleLimitsAreCumulativeAndCannotBeOverriddenByOneDocument()
    {
        var doc = Document("{}");
        var bundle = ContractBundleCodec.Create(Origin, [doc]);
        Assert.Throws<ApiException>(() => new ContractReferenceRegistry(bundle, new(MaxBundleBytes: 10)));
        Assert.Throws<ApiException>(() => new ContractReferenceRegistry(bundle, new(MaxNodes: 3)));
        Assert.Throws<ApiException>(() => new ContractReferenceRegistry(bundle, new(MaxDepth: 2)));
        var many = Enumerable.Range(0, 17).Select(i => doc with { Source = doc.Source with { LogicalUri = new Uri(Origin, i + ".json") } }).ToArray();
        Assert.Throws<ApiException>(() => ContractBundleCodec.Create(Origin, [doc, ..many]));
    }

    [Fact] public void PointerEscapesAndResourceBoundaryRemainCorrect()
    {
        var registry = new ContractReferenceRegistry(Bundle("{\"a/b~\":{\"type\":\"string\"},\"nested\":{\"$id\":\"nested.json\",\"$defs\":{\"x\":false}}}"), new());
        Assert.Equal("string", registry.Resolve(Origin, "#/components/schemas/a~1b~0").Node["type"]!.GetValue<string>());
        Assert.False(registry.Resolve(new Uri(Origin, "nested.json"), "#/$defs/x").Node.GetValue<bool>());
        Assert.Throws<ApiException>(() => registry.Resolve(Origin, "#/components/schemas/a~2b"));
    }

    [Fact] public void ExamplePayloadIdCannotRegisterSchemaResources()
    {
        var doc = Document("{\"real\":{\"$id\":\"real.json\",\"example\":{\"schema\":{\"$id\":\"fake.json\",\"$anchor\":\"hidden\"}},\"const\":{\"$id\":\"also-fake.json\"}}}");
        var registry = new ContractReferenceRegistry(ContractBundleCodec.Create(Origin, [doc]), new());
        Assert.Equal(2, registry.Resources.Count);
        Assert.Throws<ApiException>(() => registry.Resolve(Origin, "fake.json"));
        Assert.Throws<ApiException>(() => registry.Resolve(Origin, "also-fake.json"));
        Assert.Throws<ApiException>(() => registry.Resolve(new Uri(Origin, "real.json"), "#hidden"));
    }

    [Fact] public void Cumulative50000NodesAnd4MiBCanonicalBudgetAreEnforced()
    {
        var doc = Document("{\"a\":{\"enum\":[" + string.Join(',', Enumerable.Repeat("0", 12000)) + "]}}");
        var docs = Enumerable.Range(1, 4).Select(i => doc with { Source = doc.Source with { LogicalUri = new Uri(Origin, i + ".json") } }).ToArray();
        Assert.Throws<ApiException>(() => ContractBundleCodec.Create(Origin, [doc, ..docs]));
        var large = Document("{\"a\":{\"description\":\"" + new string('x', 1500000) + "\"}}");
        Assert.Throws<ApiException>(() => ContractBundleCodec.Create(Origin, [large, large with { Source = large.Source with { LogicalUri = new Uri(Origin, "two.json") } }, large with { Source = large.Source with { LogicalUri = new Uri(Origin, "three.json") } }]));
        var nested = "{}";
        for (var i = 0; i < 65; i++) nested = "{\"additionalProperties\":" + nested + "}";
        Assert.Throws<ApiException>(() => Document("{\"a\":" + nested + "}"));
    }

    [Fact] public void RegistryOwnsGraphIndependentOfMutableInput()
    {
        var bundle = Bundle("{\"a\":{\"type\":\"string\"}}");
        var registry = new ContractReferenceRegistry(bundle, new());
        bundle.Documents[0].Root["components"]!["schemas"]!["a"]!["type"] = "integer";
        Assert.Equal("string", registry.Resolve(Origin, "#/components/schemas/a").Node["type"]!.GetValue<string>());
    }

    [Fact] public void IndexedSchemaIdsCannotAmplifyAnchorKeysWithoutLimit()
    {
        var doc = Document("{\"a\":{\"$id\":\"https://contracts.example/" + new string('x', 5000) + "\",\"properties\":{\"small\":{\"$anchor\":\"small\"}}}}");
        Assert.Equal("contract_bundle_budget", Assert.Throws<ApiException>(() => new ContractReferenceRegistry(ContractBundleCodec.Create(Origin, [doc]), new())).Code);
    }

    [Fact] public void ReferenceIndexIgnoresInstanceAnnotationsAndPreservesSchemaPropertyNames()
    {
        const string text="""
        {"openapi":"3.1.0","info":{"title":"Refs","version":"1"},"paths":{},"components":{"schemas":{"A":{"type":"object","properties":{"example":{"$ref":"#/components/schemas/B"}},"default":{"$ref":"https://never-fetch.example/a"},"examples":[{"$ref":"https://never-fetch.example/b"}]},"B":{"type":"integer"}}}}
        """;
        var uri=new Uri("https://example.test/openapi");var document=new ContractDocumentReader().Read(new(uri,text,"json"),new(),default);var bundle=ContractBundleCodec.Create(uri,[document]);var registry=new ContractReferenceRegistry(bundle,new());var reference=Assert.Single(registry.References);Assert.Equal("#/components/schemas/B",reference.Reference);Assert.Equal("integer",registry.Resolve(reference.ResourceUri,reference.Reference).Node["type"]!.GetValue<string>());
    }
    [Fact] public void BundleEnvelopeDoesNotLowerTheDocumentDepthLimit()
    {
        JsonNode schema=new JsonObject{["type"]="integer"};for(var n=0;n<29;n++)schema=new JsonObject{["type"]="object",["properties"]=new JsonObject{["child"]=schema}};
        var root=JsonNode.Parse("{\"openapi\":\"3.1.0\",\"info\":{\"title\":\"Depth\",\"version\":\"1\"},\"paths\":{},\"components\":{\"schemas\":{}}}")!;root["components"]!["schemas"]!["Node"]=schema;var source=root.ToJsonString(new JsonSerializerOptions{MaxDepth=64});var doc=new ContractDocumentReader().Read(new(Origin,source,"json"),new(),default);var bundle=ContractBundleCodec.Create(Origin,[doc]);
        var json=ContractBundleCodec.Encode(bundle);Assert.Equal(bundle.Hash,ContractBundleCodec.Decode(json).Hash);
    }
}
