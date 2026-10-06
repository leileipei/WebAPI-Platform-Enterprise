using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class ComparisonPersistenceTests
{
    [Fact] public async Task DatabaseRejectsInvalidHashesCoverageAndNullRiskNote()
    {
        await using var f=new ComparisonFixture();await f.InitializeAsync();var id=await f.CreateIdAsync();await using var connection=await f.Api.Database.OpenAsync();
        foreach(var set in new[]{"coverage='Compatible'","report_hash='bad'"})
        {await using var cmd=connection.CreateCommand();cmd.CommandText="UPDATE api_version_comparisons SET "+set+" WHERE id=@id";cmd.Parameters.AddWithValue("id",id);var error=await Assert.ThrowsAsync<PostgresException>(()=>cmd.ExecuteNonQueryAsync());Assert.Equal(PostgresErrorCodes.CheckViolation,error.SqlState);}
        await using var insert=connection.CreateCommand();insert.CommandText="INSERT INTO api_version_risk_reviews (comparison_id,organization_id,project_id,api_id,input_fingerprint,report_hash,decision,comment,actor_id,created_at) SELECT id,organization_id,project_id,api_id,input_fingerprint,report_hash,'AcceptedRisk',NULL,created_by,created_at FROM api_version_comparisons WHERE id=@id";insert.Parameters.AddWithValue("id",id);var note=await Assert.ThrowsAsync<PostgresException>(()=>insert.ExecuteNonQueryAsync());Assert.Equal(PostgresErrorCodes.CheckViolation,note.SqlState);
    }
    [Fact] public async Task MigrationAddsOnlyTwoEmptyTables()
    {
        await using var f=new ComparisonFixture();await f.InitializeAsync();await using var connection=await f.Api.Database.OpenAsync();await using var command=connection.CreateCommand();command.CommandText="SELECT count(*) FROM information_schema.tables WHERE table_schema='public' AND table_name IN ('api_version_comparisons','api_version_risk_reviews')";Assert.Equal(2L,await command.ExecuteScalarAsync());command.CommandText="SELECT count(*) FROM api_version_comparisons";Assert.Equal(0L,await command.ExecuteScalarAsync());command.CommandText="SELECT count(*) FROM api_version_risk_reviews";Assert.Equal(0L,await command.ExecuteScalarAsync());await using var db=f.Api.Context();Assert.Equal(2,await db.Set<ApiVersion>().CountAsync());Assert.Equal(0,await db.Set<ReleaseRecord>().CountAsync());
    }
    [Fact] public async Task ReportSurvivesNewServiceScopeAndSourceDeletion()
    {
        await using var f=new ComparisonFixture();await f.InitializeAsync();var id=await f.CreateIdAsync();using var read=await f.Api.Client.GetAsync($"/api/v1/version-comparisons/{id}");read.EnsureSuccessStatusCode();var original=await read.Content.ReadFromJsonAsync<JsonElement>();
        await using(var db=f.Api.Context()){db.RemoveRange(await db.Set<ApiParameter>().Where(x=>x.ApiVersionId==f.Target.Id).ToArrayAsync());db.Remove(await db.Set<ApiVersion>().SingleAsync(x=>x.Id==f.Target.Id));await db.SaveChangesAsync();}
        using var missing=await f.Api.Client.GetAsync($"/api/v1/version-comparisons/{id}");missing.EnsureSuccessStatusCode();var value=await missing.Content.ReadFromJsonAsync<JsonElement>();Assert.Equal("Missing",value.GetProperty("freshness").GetString());Assert.Equal(original.GetProperty("reportHash").GetString(),value.GetProperty("reportHash").GetString());Assert.Equal(original.GetProperty("report").GetRawText(),value.GetProperty("report").GetRawText());
    }
}
