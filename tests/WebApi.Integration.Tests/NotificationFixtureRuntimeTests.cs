using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using WebApi.Integration.Tests.Support;
using WebApi.NotificationFixtureHost;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class NotificationFixtureRuntimeTests
{
 [Fact] public void SeedUsesExplicitContainerEnvironmentWithoutWeakeningProduction()
 {Assert.Equal("Development",NotificationAcceptanceCommand.CreateBuilder("Development").Environment.EnvironmentName);Assert.Equal("Production",NotificationAcceptanceCommand.CreateBuilder("Production").Environment.EnvironmentName);}
 [Fact] public void SeedGuardAcceptsOnlyExactDisposableOwnerOrganization()
 {var owner=Guid.NewGuid().ToString("D");var project="webapi-enterprise-local-test-"+Guid.NewGuid();var code="NOTIFY_"+owner.Replace("-","").ToUpperInvariant();Assert.True(NotificationAcceptanceCommand.CanSeed(project,owner,code));Assert.False(NotificationAcceptanceCommand.CanSeed("webapi-enterprise-local",owner,code));Assert.False(NotificationAcceptanceCommand.CanSeed(project,owner,"NOTIFY_FOREIGN"));}
 [Fact] public void JournalPreservesReceiptsAndRejectsForeignOwner()
 {
  var directory=Directory.CreateTempSubdirectory("notification-journal-");try{var owner=Guid.NewGuid().ToString("D");var project="webapi-enterprise-local-test-"+Guid.NewGuid();var id=Guid.NewGuid();var journal=new FixtureJournal(directory.FullName,owner,project);var smtp=new SmtpFixtureReceipt(Guid.NewGuid(),id,id+"@webapi.invalid",1,true,true,true,250,new string('a',64),0,0,DateTimeOffset.UtcNow);var webhook=new WebhookFixtureReceipt(id,"123",true,true,"Tls13",new string('a',64),10,202,true,false,DateTimeOffset.UtcNow);journal.Append(smtp);journal.Append(webhook);var restored=new FixtureJournal(directory.FullName,owner,project);Assert.Equal(smtp,Assert.Single(restored.Smtp));Assert.Equal(webhook,Assert.Single(restored.Webhook));Assert.Throws<InvalidOperationException>(()=>new FixtureJournal(directory.FullName,Guid.NewGuid().ToString("D"),project));}finally{directory.Delete(true);}
 }
 [Fact] public void JournalRebindRetainsAcceptedDedupAndRejectsWrongOldOwner()
 {
  var directory=Directory.CreateTempSubdirectory("notification-rebind-");try{var owner=Guid.NewGuid().ToString("D");var project="webapi-enterprise-local-test-"+Guid.NewGuid();var next=Guid.NewGuid().ToString("D");var nextProject="webapi-enterprise-local-test-"+Guid.NewGuid();var journal=new FixtureJournal(directory.FullName,owner,project);var id=Guid.NewGuid();journal.Append(new WebhookFixtureReceipt(id,"123",true,true,"Tls13",new string('a',64),10,202,true,false,DateTimeOffset.UtcNow));Assert.Throws<InvalidOperationException>(()=>FixtureJournal.Rebind(directory.FullName,Guid.NewGuid().ToString("D"),project,next,nextProject));FixtureJournal.Rebind(directory.FullName,owner,project,next,nextProject);var restored=new FixtureJournal(directory.FullName,next,nextProject);Assert.Equal(id,Assert.Single(restored.Webhook).DeliveryId);var webhook=new WebhookFixture(new(new byte[32]),TimeProvider.System,restored);Assert.Equal(1,webhook.BusinessAcceptCount);}finally{directory.Delete(true);}
 }
 [Fact] public async Task FixtureControlRejectsMissingWrongOwnerAndPersistsReceiptAcrossRestart()
 {
  var directory=Directory.CreateTempSubdirectory("notification-owned-");try{var owner=Guid.NewGuid().ToString("D");var project="webapi-enterprise-local-test-"+Guid.NewGuid();var token=Guid.NewGuid().ToString("N");var tokenFile=Path.Combine(directory.FullName,"token");await File.WriteAllTextAsync(tokenFile,token);
  await using var fixture=new NotificationTransportFixture();await fixture.InitializeAsync(configure:builder=>{builder.Configuration["Fixture:OwnerId"]=owner;builder.Configuration["Fixture:ProjectName"]=project;builder.Configuration["Fixture:OwnerTokenFile"]=tokenFile;builder.Configuration["Fixture:JournalDirectory"]=Path.Combine(directory.FullName,"journal");});
  using var handler=new HttpClientHandler{ServerCertificateCustomValidationCallback=(_,_,_,_)=>true};using var http=new HttpClient(handler){BaseAddress=new Uri("https://127.0.0.1:"+fixture.WebhookUrl.Port)};
  using(var absent=await http.GetAsync("/owner/observations"))Assert.Equal(HttpStatusCode.NotFound,absent.StatusCode);
  http.DefaultRequestHeaders.Add("X-WebAPI-Fixture-Owner","wrong");using(var wrong=await http.PostAsJsonAsync("/owner/mode",new{smtp="Temporary",webhook="RateLimited",retryAfter="7200"}))Assert.Equal(HttpStatusCode.NotFound,wrong.StatusCode);http.DefaultRequestHeaders.Remove("X-WebAPI-Fixture-Owner");http.DefaultRequestHeaders.Add("X-WebAPI-Fixture-Owner",token);
  using(var control=await http.PostAsJsonAsync("/owner/mode",new{smtp="Temporary",webhook="RateLimited",retryAfter="7200"}))Assert.Equal(HttpStatusCode.OK,control.StatusCode);
  using(var extra=await http.PostAsJsonAsync("/owner/mode",new{smtp="Accept",webhook="Accept",target="https://foreign.example"}))Assert.Equal(HttpStatusCode.BadRequest,extra.StatusCode);
  using var observed=await http.GetAsync("/owner/observations");Assert.Equal(HttpStatusCode.OK,observed.StatusCode);var document=await observed.Content.ReadFromJsonAsync<JsonElement>();Assert.Equal(owner,document.GetProperty("ownerId").GetString());Assert.Null(document.GetProperty("sourceRevision").GetString());Assert.DoesNotContain(token,document.GetRawText());
  var id=Guid.NewGuid();fixture.WebhookJournal.Append(new WebhookFixtureReceipt(id,"123",true,true,"Tls13",new string('a',64),10,202,true,false,DateTimeOffset.UtcNow));var restored=new FixtureJournal(Path.Combine(directory.FullName,"journal"),owner,project);Assert.Equal(id,Assert.Single(restored.Webhook).DeliveryId);
  }finally{directory.Delete(true);}
 }
}
