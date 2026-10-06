using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Common;
using WebApi.Contracts.OpenApi;
using WebApi.Infrastructure.Contracts;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class ComparisonSourceSnapshotTests
{
    [Fact]public async Task SavedIndependentMaintenanceSupportsAnotherSaveAndValidation()
    {
        var setup=await SchemaValidationApiTests.Setup();await using var f=setup.Fixture;var path=$"/api/v1/versions/{f.Version.Id}/parameters";
        using var first=await f.WriteAsync(HttpMethod.Put,path,new[]{new WebApi.Contracts.Catalog.SaveParameterRequest(setup.Parameter.Id,"query","q","integer",false,"{\"type\":\"integer\"}")},"\"1\"");Assert.True(first.IsSuccessStatusCode,await first.Content.ReadAsStringAsync());
        string bundle;await using(var db=f.Context())bundle=(await db.Set<ApiVersionContractSources>().SingleAsync()).BundleJson;
        using var second=await f.WriteAsync(HttpMethod.Put,path,new[]{new WebApi.Contracts.Catalog.SaveParameterRequest(setup.Parameter.Id,"query","q","number",false,"{\"type\":\"number\"}")},"\"2\"");Assert.True(second.IsSuccessStatusCode,await second.Content.ReadAsStringAsync());
        using var validation=await f.WriteAsync(HttpMethod.Post,SchemaValidationApiTests.Path(f),SchemaValidationApiTests.Body(null,"0.5",3,parameterId:setup.Parameter.Id));Assert.True(validation.IsSuccessStatusCode,await validation.Content.ReadAsStringAsync());Assert.Equal("Valid",(await validation.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetProperty("status").GetString());
        await using var check=f.Context();Assert.Equal(bundle,(await check.Set<ApiVersionContractSources>().SingleAsync()).BundleJson);Assert.Equal(3,(await check.Set<ApiVersion>().SingleAsync()).Revision);
    }
    [Fact]public async Task StrictComparisonModeIsFrozenAndRemainsCurrent()
    {
        await using var f=new ComparisonFixture();await f.InitializeAsync();
        using var response=await f.CreateAsync(request:new{fromVersionId=f.Api.Version.Id,toVersionId=f.Target.Id,expectedFromRevision=1,expectedToRevision=1,formatMode="Strict"});response.EnsureSuccessStatusCode();var body=await response.Content.ReadFromJsonAsync<JsonElement>();var id=body.GetProperty("id").GetGuid();
        Assert.Equal("Strict",body.GetProperty("report").GetProperty("provenance").GetProperty("formatMode").GetString());
        using var read=await f.Api.Client.GetAsync($"/api/v1/version-comparisons/{id}");read.EnsureSuccessStatusCode();Assert.Equal("Current",(await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("freshness").GetString());
        using var annotation=await f.CreateAsync();annotation.EnsureSuccessStatusCode();Assert.NotEqual(body.GetProperty("report").GetProperty("inputFingerprint").GetString(),(await annotation.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("report").GetProperty("inputFingerprint").GetString());
    }
    [Fact]public async Task UnregisteredComparisonFormatModeIsRejectedWithoutEvidenceWrite()
    {
        await using var f=new ComparisonFixture();await f.InitializeAsync();using var response=await f.CreateAsync(request:new{fromVersionId=f.Api.Version.Id,toVersionId=f.Target.Id,expectedFromRevision=1,expectedToRevision=1,formatMode="UnknownMode"});Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity,response.StatusCode);
        await using var db=f.Api.Context();Assert.Empty(await db.Set<ApiVersionComparison>().ToArrayAsync());
    }
    [Fact]public async Task NewEvidenceStoresImmutableSourcesAndRawInputDigest()
    {
        await using var f=new ComparisonFixture();await f.InitializeAsync();var reader=new ContractDocumentReader();
        await using(var db=f.Api.Context())foreach(var id in new[]{f.Api.Version.Id,f.Target.Id}){
            var version=await db.Set<ApiVersion>().SingleAsync(x=>x.Id==id);var raw="{\"openapi\":\"3.1.0\",\"info\":{\"title\":\"Sources\",\"version\":\"1\"},\"paths\":{}}";version.OpenapiDocument=raw;var uri=new Uri($"https://fixed.invalid/{id:D}.json");var document=reader.Read(new(uri,raw,"json"),new(),default);var bundle=ContractBundleCodec.Create(uri,[document]);
            var documentHash=WebApi.Infrastructure.Comparisons.ContractNormalizer.Hash(System.Text.Encoding.UTF8.GetBytes(document.CanonicalJson));
            var metadata=new ContractSourceMetadata([new(uri,"json")],[],documentHash);
            db.Add(new ApiVersionContractSources{ApiVersionId=id,BundleJson=ContractBundleCodec.Encode(bundle),BundleHash=bundle.Hash,Dialect="Oas31",SourcesJson=JsonSerializer.Serialize(metadata,CanonicalJson.Options)});await db.SaveChangesAsync();
        }
        var comparisonId=await f.CreateIdAsync();await using(var db=f.Api.Context()){
            var entity=await db.Set<ApiVersionComparison>().SingleAsync(x=>x.Id==comparisonId);var input=JsonDocument.Parse(entity.InputBytes).RootElement;
            Assert.True(input.GetProperty("from").TryGetProperty("sources",out var source));Assert.NotEmpty(source.GetProperty("bundleHash").GetString()!);Assert.Equal("Annotation",input.GetProperty("formatMode").GetString());
            var report=JsonDocument.Parse(entity.ReportBytes).RootElement;Assert.Equal(WebApi.Infrastructure.Comparisons.ContractNormalizer.Hash(entity.InputBytes),report.GetProperty("provenance").GetProperty("inputHash").GetString());
            (await db.Set<ApiVersionContractSources>().SingleAsync(x=>x.ApiVersionId==f.Target.Id)).BundleHash="tampered";await db.SaveChangesAsync();
        }
        using var read=await f.Api.Client.GetAsync($"/api/v1/version-comparisons/{comparisonId}");read.EnsureSuccessStatusCode();Assert.Equal("Stale",(await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("freshness").GetString());
    }
    [Fact]public async Task ComparisonApiFailsClosedWhenItsChildCannotStart()
    {
        await using var api=new ApiFixture();await api.InitializeAsync(builder=>builder.Services.AddSingleton(new SchemaValidationSettings("/missing-test-tool.dll")));await api.SeedCatalogAsync();var target=new ApiVersion{ApiId=api.Api.Id,Version="2",CreatedBy=api.User.Id};await using(var db=api.Context()){db.Add(target);await db.SaveChangesAsync();}
        using var login=await api.LoginAsync();login.EnsureSuccessStatusCode();using var response=await ApiFixture.CommandAsync(api.Client,$"/api/v1/apis/{api.Api.Id}/version-comparisons",new{fromVersionId=api.Version.Id,toVersionId=target.Id,expectedFromRevision=1,expectedToRevision=1});response.EnsureSuccessStatusCode();var report=(await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("report");Assert.Equal("Invalid",report.GetProperty("coverage").GetString());Assert.Contains(report.GetProperty("coverageIssues").EnumerateArray(),x=>x.GetProperty("code").GetString()!.StartsWith("contract_process_",StringComparison.Ordinal));Assert.Equal(0,report.GetProperty("findings").GetArrayLength());
    }
}
