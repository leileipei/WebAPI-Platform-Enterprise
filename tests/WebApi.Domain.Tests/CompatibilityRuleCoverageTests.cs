using System.Text.Json;
using WebApi.Infrastructure.Comparisons.Rules;
using Xunit;
using Xunit.Abstractions;
namespace WebApi.Domain.Tests;
public sealed class CompatibilityRuleCoverageTests(ITestOutputHelper log)
{
    [Fact] public void EveryRuleHasExecutedDirectionalCasesAndCoverageOutput()
    {
        var catalog=CompatibilityRuleCatalog.All;Assert.NotEmpty(catalog);Assert.Equal(catalog.Count,catalog.Select(x=>x.Id).Distinct(StringComparer.Ordinal).Count());
        using var fixtures=JsonDocument.Parse(File.ReadAllText(CompatibilityProofTests.FixturePath));var rows=fixtures.RootElement.EnumerateArray().ToArray();var output=new List<object>();
        foreach(var descriptor in catalog){
            var cases=rows.Where(x=>descriptor.TestIds.Contains(x.GetProperty("id").GetString()!)).ToArray();
            if(descriptor.Id=="schema.references"){var test=new CompatibilityProofTests();test.FiniteExternalReferencesRespectEachFixedBundle();test.RecursiveRefTerminatesAndUnknownIsVisible();test.MissingReferenceIsNeverCompatibleEvenWhenTextIsEqual();output.Add(new{descriptor.Id,count=3,testIds=descriptor.TestIds});continue;}
            if(descriptor.Id=="schema.dynamic_references"){new CompatibilityProofTests().DynamicScopeChangeIsExplicitlyUnknown();output.Add(new{descriptor.Id,count=1,testIds=descriptor.TestIds});continue;}
            Assert.NotEmpty(cases);var executed=0;foreach(var value in cases)foreach(var direction in new[]{"request","response"}){var result=CompatibilityProofTests.Compare(value.GetProperty("baseline").GetRawText(),value.GetProperty("target").GetRawText(),direction,value.GetProperty("version").GetString()!);Assert.Equal(value.GetProperty(direction).GetString(),result.Risk);executed++;}output.Add(new{descriptor.Id,count=executed,testIds=cases.SelectMany(value=>new[]{"request","response"}.Select(direction=>value.GetProperty("id").GetString()+"/"+direction)).ToArray()});
        }
        var outputPath=Path.Combine(AppContext.BaseDirectory,"compatibility-rule-coverage.json");File.WriteAllText(outputPath,JsonSerializer.Serialize(output,new JsonSerializerOptions{WriteIndented=true}));
        Assert.Equal(catalog.Count,JsonDocument.Parse(File.ReadAllText(outputPath)).RootElement.GetArrayLength());log.WriteLine("CONTRACT_RULE_COVERAGE="+JsonSerializer.Serialize(output));
    }
    [Fact] public void EveryDeclaredKeywordHasAVisibleRuleFamily()
    {
        string[] keywords=["type","nullable","enum","const","multipleOf","minimum","maximum","exclusiveMinimum","exclusiveMaximum","minLength","maxLength","pattern","format","properties","required","additionalProperties","patternProperties","propertyNames","minProperties","maxProperties","items","prefixItems","minItems","maxItems","uniqueItems","contains","minContains","maxContains","allOf","anyOf","oneOf","not","if","then","else","dependentRequired","dependentSchemas","unevaluatedProperties","unevaluatedItems","$ref","$id","$anchor","$dynamicRef","$dynamicAnchor","$defs","definitions","$schema","$vocabulary","readOnly","writeOnly","discriminator","xml","title","description","default","examples","example","deprecated","externalDocs","contentEncoding","contentMediaType","contentSchema"];
        foreach(var keyword in keywords){Assert.True(CompatibilityRuleCatalog.KeywordRules.TryGetValue(keyword,out var id),keyword);Assert.Contains(CompatibilityRuleCatalog.All,x=>x.Id==id);}
    }
}
