using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore.Diagnostics;
using WebApi.Infrastructure.Persistence;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Releases;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class VerificationReportTests
{
    [Theory] [InlineData(0,HttpStatusCode.OK,false)] [InlineData(1,HttpStatusCode.RequestEntityTooLarge,false)] [InlineData(0,HttpStatusCode.OK,true)] [InlineData(1,HttpStatusCode.RequestEntityTooLarge,true)]
    public async Task ActualBytesAreLimitedToTenMiB(int extra,HttpStatusCode expected,bool chunked)
    {await using var s=new DeliveryScenario();await s.InitializeAsync();var bytes=Enumerable.Repeat((byte)'a',10*1024*1024+extra).ToArray();using var response=await s.UploadAsync(bytes,chunked:chunked);Assert.Equal(expected,response.StatusCode);if(extra==0)Assert.Equal(bytes.Length,(await response.Content.ReadFromJsonAsync<VerificationReportDto>())!.SizeBytes);}
    [Theory] [InlineData("application/pdf","plain text")] [InlineData("text/html","<html><script>alert(1)</script></html>")] [InlineData("text/plain","<html><script>alert(1)</script></html>")] [InlineData("application/octet-stream","binary")]
    public async Task FakeMimeAndExecutableHtmlAreRejected(string mime,string text)
    {await using var s=new DeliveryScenario();await s.InitializeAsync();using var response=await s.UploadAsync(Encoding.UTF8.GetBytes(text),mime);Assert.Equal(HttpStatusCode.UnprocessableEntity,response.StatusCode);}
    [Fact] public async Task TextMustBeStrictUtf8()
    {await using var s=new DeliveryScenario();await s.InitializeAsync();using var response=await s.UploadAsync([0xc3,0x28]);Assert.Equal(HttpStatusCode.UnprocessableEntity,response.StatusCode);}
    [Fact] public async Task PdfDownloadIsAttachmentAndEvidenceStoresOnlyTheDigest()
    {await using var s=new DeliveryScenario();await s.InitializeAsync();var bytes=Encoding.UTF8.GetBytes("%PDF-1.7\nprivate report body\n%%EOF");using var upload=await s.UploadAsync(bytes,"application/pdf");upload.EnsureSuccessStatusCode();var report=(await upload.Content.ReadFromJsonAsync<VerificationReportDto>())!;Assert.Equal(64,report.Sha256.Length);using var download=await s.Api.Client.GetAsync($"/api/v1/verification-reports/{report.Id}/download");download.EnsureSuccessStatusCode();Assert.Equal(bytes,await download.Content.ReadAsByteArrayAsync());Assert.Equal("attachment",download.Content.Headers.ContentDisposition!.DispositionType);Assert.Contains("nosniff",download.Headers.GetValues("X-Content-Type-Options"));Assert.Contains("no-store",download.Headers.CacheControl!.ToString());using var fact=await s.RecordAsync(report:report.Id);fact.EnsureSuccessStatusCode();Assert.Equal(report.Sha256,(await fact.Content.ReadFromJsonAsync<ReleaseVerificationDto>())!.ReportHash);await using var db=s.Api.Context();Assert.DoesNotContain("private report body",string.Join("",await db.Set<AuditLog>().Select(a=>a.AfterJson).ToArrayAsync()));}
    [Fact] public async Task DownloadRechecksCurrentContractVisibility()
    {await using var s=new DeliveryScenario();await s.InitializeAsync();using var upload=await s.UploadAsync(Encoding.UTF8.GetBytes("人工测试结果"));upload.EnsureSuccessStatusCode();var report=(await upload.Content.ReadFromJsonAsync<VerificationReportDto>())!;await using(var db=s.Api.Context()){var permission=await db.Set<Permission>().SingleAsync(p=>p.Code=="api.schema.read");await db.Set<RolePermission>().Where(x=>x.PermissionId==permission.Id).ExecuteDeleteAsync();}using var download=await s.Api.Client.GetAsync($"/api/v1/verification-reports/{report.Id}/download");Assert.Equal(HttpStatusCode.NotFound,download.StatusCode);}
    [Fact] public async Task UploadRequiresExactlyOneServerResolvedOwnerAndDedicatedPermission()
    {await using var s=new DeliveryScenario();await s.InitializeAsync(false);using var denied=await s.UploadAsync(Encoding.UTF8.GetBytes("report"));Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);using var foreign=await s.UploadAsync(Encoding.UTF8.GetBytes("report"),artifact:Guid.NewGuid());Assert.Equal(HttpStatusCode.NotFound,foreign.StatusCode);}
    [Fact] public async Task AnotherArtifactReportCannotBeReattachedToSourceEvidence()
    {await using var s=new DeliveryScenario();await s.InitializeAsync();await using var other=new DeliveryScenario();await other.InitializeAsync();using var upload=await other.UploadAsync(Encoding.UTF8.GetBytes("other report"));upload.EnsureSuccessStatusCode();var report=(await upload.Content.ReadFromJsonAsync<VerificationReportDto>())!;using var response=await s.RecordAsync(report:report.Id);Assert.Equal(HttpStatusCode.NotFound,response.StatusCode);}
    [Fact] public async Task LostCommitAcknowledgementDoesNotDeleteACommittedReport()
    {
        var fault=new LostCommitReceipt();await using var s=new DeliveryScenario();await s.InitializeAsync(configure:b=>b.Services.AddDbContext<WebApiDbContext>(o=>o.AddInterceptors(fault)));
        fault.Enabled=true;var bytes=Encoding.UTF8.GetBytes("committed report body");var key=Guid.NewGuid().ToString("N");using var failed=await s.UploadAsync(bytes,key:key);Assert.Equal(HttpStatusCode.InternalServerError,failed.StatusCode);
        await using var db=s.Api.Context();var metadata=await db.Set<VerificationReport>().SingleAsync();using var download=await s.Api.Client.GetAsync($"/api/v1/verification-reports/{metadata.Id}/download");download.EnsureSuccessStatusCode();Assert.Equal(bytes,await download.Content.ReadAsByteArrayAsync());
        using var replay=await s.UploadAsync(bytes,key:key);replay.EnsureSuccessStatusCode();Assert.Equal(metadata.Id,(await replay.Content.ReadFromJsonAsync<VerificationReportDto>())!.Id);
    }
    private sealed class LostCommitReceipt:DbTransactionInterceptor
    {
        public bool Enabled;
        public override Task TransactionCommittedAsync(System.Data.Common.DbTransaction transaction,TransactionEndEventData eventData,CancellationToken ct=default)
        {if(Enabled){Enabled=false;throw new IOException("simulated lost commit receipt after actual database commit");}return Task.CompletedTask;}
    }
}
