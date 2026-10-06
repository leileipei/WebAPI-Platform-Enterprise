using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.OpenApi;
using WebApi.Infrastructure.Contracts;
using Xunit;
namespace WebApi.Domain.Tests;
public class OfficialJsonSchemaTests
{
    private static string FixtureDirectory=>Path.Combine(AppContext.BaseDirectory,"fixtures","json-schema-2020-12");
    private static readonly Lazy<Dictionary<string,string>> Remotes=new(()=>Directory.GetFiles(Path.Combine(FixtureDirectory,"remotes"),"*.json",SearchOption.AllDirectories).ToDictionary(x=>"http://localhost:1234/"+Path.GetRelativePath(Path.Combine(FixtureDirectory,"remotes"),x).Replace('\\','/'),File.ReadAllText,StringComparer.Ordinal));
    private sealed record Case(string Id,string Schema,string Data,bool Valid,bool Strict);
    private static IEnumerable<Case> Cases(bool strict=false)
    {
        var directory=strict?Path.Combine(FixtureDirectory,"optional","format"):FixtureDirectory;
        foreach(var file in Directory.GetFiles(directory,"*.json").Order(StringComparer.Ordinal)) {
            using var groups=JsonDocument.Parse(File.ReadAllText(file));var groupIndex=0;
            foreach(var group in groups.RootElement.EnumerateArray()) {var testIndex=0;foreach(var test in group.GetProperty("tests").EnumerateArray())yield return new(Path.GetFileName(file)+":"+groupIndex+":"+testIndex++,group.GetProperty("schema").GetRawText(),test.GetProperty("data").GetRawText(),test.GetProperty("valid").GetBoolean(),strict);groupIndex++;}
        }
    }
    [Fact]public void FixedFixtureBytesAndAll1301RootCasesArePresent()
    {
        using var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"fixtures","json-schema-manifest.json")));
        Assert.Equal("f6fd52a0a95472e079cbfc6ef7f089702b80e045",manifest.RootElement.GetProperty("commit").GetString());
        foreach(var file in manifest.RootElement.GetProperty("files").EnumerateObject())Assert.Equal(file.Value.GetProperty("sha256").GetString(),Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(FixtureDirectory,file.Name)))));
        Assert.Equal(1301,Cases().Count());Assert.Equal(46,Directory.GetFiles(FixtureDirectory,"*.json").Length);
    }
    [Fact]public void RequiredRefAndDynamicRefUseTheProductionAdapter()
    {
        var failures=new List<string>();
        foreach(var test in Cases().Where(x=>x.Id.StartsWith("required.json:",StringComparison.Ordinal)||x.Id.StartsWith("ref.json:",StringComparison.Ordinal)||x.Id.StartsWith("dynamicRef.json:",StringComparison.Ordinal))) {
            var result=EvaluateProduct(test);if(result.Status!=(test.Valid?"Valid":"Invalid"))failures.Add(test.Id+" expected="+test.Valid+" actual="+result.Status+" coverage="+string.Join(',',result.CoverageIssues.Select(x=>x.Code)));
        }
        Assert.True(failures.Count==0,string.Join('\n',failures));
    }
    [Fact]public void AllOfficialRootAndRegisteredStrictCasesRunInAnOfflineEngine()
    {
        var failures=new List<string>();var rootCount=0;var strictCount=0;
        foreach(var test in Cases().Concat(Cases(true))) {
            if(test.Strict)strictCount++;else rootCount++;
            try {
                var registry=new SchemaRegistry();
                var options=new BuildOptions{SchemaRegistry=registry,VocabularyRegistry=new(),DialectRegistry=new(),Dialect=Dialect.Draft202012};
                // Test-only resolution reads immutable fixture bytes, never HTTP. Unknown URIs throw
                // before the library can use its global Fetch fallback.
                registry.Fetch=(uri,_)=>Remotes.Value.TryGetValue(uri.AbsoluteUri.Split('#')[0],out var source)?JsonSchema.FromText(source,options,uri):throw new InvalidOperationException("Unregistered official fixture");
                var schema=JsonSchema.FromText(test.Schema,options,new Uri("http://localhost:1234/schema"));
                using var data=JsonDocument.Parse(test.Data);
                var result=schema.Evaluate(data.RootElement,new(){OutputFormat=OutputFormat.Flag,RequireFormatValidation=test.Strict,FormatRegistry=ContractFormats.Create()});
                if(result.IsValid!=test.Valid)failures.Add(test.Id+" expected="+test.Valid+" actual="+result.IsValid);
            }catch(Exception error){failures.Add(test.Id+" exception="+error.GetType().Name);}
        }
        Assert.Equal(1301,rootCount);Assert.Equal(842,strictCount);Assert.True(failures.Count==0,string.Join('\n',failures));
    }
    [Fact]public void AllRegisteredDialectRootCasesUseTheProductionAdapter()
    {
        var failures=new List<string>();var count=0;
        foreach(var test in Cases()) {
            count++;
            try {
                var result=EvaluateProduct(test);
                // These five cases deliberately declare two custom vocabularies outside the
                // product dialect contract; the full engine test above still evaluates them.
                var expected=test.Id.StartsWith("vocabulary.json:",StringComparison.Ordinal)?"Incomplete":test.Valid?"Valid":"Invalid";
                if(result.Status!=expected)failures.Add(test.Id+" expected="+expected+" actual="+result.Status+" coverage="+string.Join(',',result.CoverageIssues.Select(x=>x.Code)));
            }catch(Exception error){failures.Add(test.Id+" exception="+error.GetType().Name);}
        }
        Assert.Equal(1301,count);Assert.True(failures.Count==0,string.Join('\n',failures));
    }
    [Fact]public void RegisteredStrictFormatCasesUseTheProductionAdapter()
    {
        var failures=new List<string>();var count=0;
        foreach(var test in Cases(true).Where(x=>ContractFormats.Registered.Contains(x.Id.Split('.')[0]))) {
            count++;var result=EvaluateProduct(test);var expected=test.Valid?"Valid":"Invalid";
            if(result.Status!=expected)failures.Add(test.Id+" expected="+expected+" actual="+result.Status);
        }
        Assert.Equal(842,count);Assert.True(failures.Count==0,string.Join('\n',failures));
    }
    private static SchemaValidationResult EvaluateProduct(Case test)
    {
        var uri=new Uri("http://localhost:1234/schema");var rootUri=new Uri("https://official.invalid/openapi");var reader=new ContractDocumentReader();var limits=new ContractLimits();
        var root=reader.Read(new(rootUri,"{\"openapi\":\"3.1.0\",\"info\":{\"title\":\"Official\",\"version\":\"1\"},\"paths\":{}}","json"),limits,default);
        var schema=reader.ReadResource(new(uri,test.Schema,"json"),limits,ContractDialect.Oas31,default);var docs=new List<ContractDocument>{root,schema};
        var seen=new HashSet<string>(StringComparer.Ordinal){uri.AbsoluteUri};var pending=new Queue<ContractSource>();pending.Enqueue(schema.Source);
        while(pending.TryDequeue(out var document)) {
            foreach(var reference in ReferenceUris(JsonNode.Parse(document.RawText),document.LogicalUri)) {
                var key=reference.AbsoluteUri.Split('#')[0];if(seen.Add(key)&&Remotes.Value.TryGetValue(key,out var text)) {
                    var source=new ContractSource(new Uri(key),text,"json");docs.Add(reader.ReadResource(source,limits,ContractDialect.Oas31,default));pending.Enqueue(source);
                }
            }
        }
        var input=new SchemaValidationInput(ContractBundleCodec.Create(rootUri,docs,limits),JsonNode.Parse(test.Schema)!,JsonNode.Parse(test.Data),"request",test.Strict?"Strict":"Annotation",true,uri);
        return new SchemaEvaluator().Evaluate(input,limits,default);
    }
    private static IEnumerable<Uri> ReferenceUris(JsonNode? node,Uri basis)
    {
        if(node is JsonObject obj) {
            if(obj["$id"] is JsonValue id&&id.TryGetValue<string>(out var idText))basis=new(basis,idText);
            foreach(var(keyword,value)in obj) {
                if(keyword is "$ref" or "$dynamicRef"&&value is JsonValue reference&&reference.TryGetValue<string>(out var text))yield return new(basis,text);
                else if(keyword is not("enum" or "const" or "default" or "examples" or "example"))foreach(var child in ReferenceUris(value,basis))yield return child;
            }
        }else if(node is JsonArray array)foreach(var child in array)foreach(var reference in ReferenceUris(child,basis))yield return reference;
    }
}
