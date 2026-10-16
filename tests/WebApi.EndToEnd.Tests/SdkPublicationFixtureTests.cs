using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
namespace WebApi.EndToEnd.Tests;

// Protocol regression with a fake HTTP handler. This is a unit fixture, not live SDK acceptance.
public sealed class SdkPublicationFixtureTests
{
    private sealed class FixtureHandler:HttpMessageHandler
    {
        public Guid ApiId {get;}=Guid.NewGuid();public Guid EnvironmentId {get;}=Guid.NewGuid();public Guid ClusterId {get;}=Guid.NewGuid();
        public Guid OldVersion {get;}=Guid.NewGuid();public Guid NewVersion {get;}=Guid.NewGuid();public Guid OtherVersion {get;}=Guid.NewGuid();
        public Guid OwnRoute {get;}=Guid.NewGuid();public Guid OtherRoute {get;}=Guid.NewGuid();public Guid Destination {get;}=Guid.NewGuid();public Guid Release {get;}=Guid.NewGuid();
        public List<Guid> UpdatedRoutes {get;}=[];public bool Previewed {get;private set;}public JsonElement? ReleaseBody {get;private set;}
        public JsonElement Context=>JsonSerializer.SerializeToElement(new{apiId=ApiId,environmentId=EnvironmentId,clusterId=ClusterId});
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {
            var path=request.RequestUri!.AbsolutePath;
            if(path=="/api/v1/auth/csrf")return Json(new{token="fixture-csrf"});
            if(path==$"/api/v1/apis/{ApiId}/versions")return request.Method==HttpMethod.Get?Json(new[]{new{id=OldVersion,revision=1,version="1.0.0"},new{id=NewVersion,revision=1,version="2.0.0"}}):Json(new{id=NewVersion,revision=1,version="2.0.0"});
            if(path==$"/api/v1/clusters/{ClusterId}")return Json(new{id=ClusterId,destinations=new[]{new{id=Destination,revision=1}}});
            if(path==$"/api/v1/destinations/{Destination}")return Json(new{id=Destination,revision=2});
            if(path==$"/api/v1/environments/{EnvironmentId}/routes")return Json(new{items=new[]{new{id=OtherRoute,apiVersionId=OtherVersion,routeName="Unrelated",clusterId=Guid.NewGuid(),revision=9},new{id=OwnRoute,apiVersionId=OldVersion,routeName="Orders",clusterId=ClusterId,revision=3}},total=2,page=1,pageSize=100});
            if(path.StartsWith("/api/v1/routes/",StringComparison.Ordinal)){UpdatedRoutes.Add(Guid.Parse(path.Split('/').Last()));return Json(new{id=OwnRoute,revision=4});}
            if(path==$"/api/v1/environments/{EnvironmentId}")return Json(new{desiredConfigVersion=7});
            if(path==$"/api/v1/environments/{EnvironmentId}/releases/preview"){Previewed=true;return Json(new{resourceRevisions=new[]{new{type="version",id=NewVersion,revision=1L},new{type="route",id=OwnRoute,revision=4L},new{type="cluster",id=ClusterId,revision=2L}}});}
            if(path==$"/api/v1/environments/{EnvironmentId}/releases"){ReleaseBody=await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);return Json(new{id=Release});}
            if(path==$"/api/v1/releases/{Release}")return Json(new{id=Release,state="Succeeded",targets=new[]{new{acknowledged=true},new{acknowledged=true}},deploymentSequence=8});
            if(path.StartsWith($"/api/v1/releases/{Release}/",StringComparison.Ordinal))return Json(new{id=Release});
            throw new InvalidOperationException("Unexpected fixture path "+path);
        }
        private static HttpResponseMessage Json(object value)=>new(HttpStatusCode.OK){Content=JsonContent.Create(value)};
    }
    [Fact]
    public async Task Legacy_SDK_publish_uses_complete_frozen_preview_and_never_updates_an_unrelated_route()
    {
        using var handler=new FixtureHandler();HttpClient Client()=>new(handler,false){BaseAddress=new("http://control-plane:8080")};
        await using var scenario=new ExternalScenario(Client(),handler.Context,[Client(),Client()]);await scenario.PublishAsync("A");
        Assert.Equal(new[]{handler.OwnRoute},handler.UpdatedRoutes);Assert.True(handler.Previewed);Assert.NotNull(handler.ReleaseBody);
        var revisions=handler.ReleaseBody.Value.GetProperty("resourceRevisions").EnumerateArray().ToArray();Assert.Equal(3,revisions.Length);Assert.Contains(revisions,r=>r.GetProperty("type").GetString()=="route"&&r.GetProperty("id").GetGuid()==handler.OwnRoute);Assert.Equal(7,handler.ReleaseBody.Value.GetProperty("baseConfigVersion").GetInt64());
    }
}
