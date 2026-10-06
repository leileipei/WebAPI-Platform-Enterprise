using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Comparisons;
using WebApi.Infrastructure.Comparisons;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class ComparisonExportTests
{
    [Fact] public async Task ExportsNeverRevealExamplesOrRawDocument()
    {await using var f=new ComparisonFixture();await f.InitializeAsync();await using(var db=f.Api.Context()){(await db.Set<ApiVersion>().SingleAsync(x=>x.Id==f.Target.Id)).OpenapiDocument="{\"openapi\":\"3.0.3\",\"info\":{\"title\":\"RAW_PRIVATE_MARKER\"},\"paths\":{}}";foreach(var p in await db.Set<ApiParameter>().Where(x=>x.ApiVersionId==f.Target.Id).ToArrayAsync())p.Schema="{\"type\":\"string\",\"example\":\"EXAMPLE_PRIVATE_MARKER\"}";await db.SaveChangesAsync();}var id=await f.CreateIdAsync();foreach(var suffix in new[]{"","/export?format=json","/export?format=csv"}){using var r=await f.Api.Client.GetAsync($"/api/v1/version-comparisons/{id}"+suffix);r.EnsureSuccessStatusCode();var content=await r.Content.ReadAsStringAsync();Assert.DoesNotContain("EXAMPLE_PRIVATE_MARKER",content);Assert.DoesNotContain("RAW_PRIVATE_MARKER",content);}}
    [Theory][InlineData("=")][InlineData("+")][InlineData("-")][InlineData("@")]
    public void CsvGuardHandlesLeadingWhitespaceAndControlCharacters(string prefix)
    {var report=new ComparisonReport("v","f","Limited",new(0,0,0,0,0,1),[],[new("unsupported","source","/"," \t\u0001"+prefix+"formula,\"quote\"\nnext")]);var v=new ComparisonVersion(Guid.NewGuid(),"1","Draft","compatible",1);var csv=Encoding.UTF8.GetString(ComparisonExporter.Export(new(Guid.NewGuid(),Guid.NewGuid(),v,v,"h",report,DateTimeOffset.UtcNow,"Current"),"csv").Bytes);Assert.Contains("\"' \t\u0001"+prefix,csv);Assert.Contains("\"\"quote\"\"",csv);Assert.Contains("CoverageIssue",csv);Assert.Contains("Summary",csv);}
}
