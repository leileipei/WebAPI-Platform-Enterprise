using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using WebApi.Gateway.Policies;
using Xunit;
namespace WebApi.Gateway.Tests;
public sealed class BoundedResponseCaptureTests
{
    [Theory][InlineData(false)][InlineData(true)]public async Task StreamAndBodyWriterCaptureAreBoundedAndRestoreOriginalFeature(bool pipe)
    {
        var context=new DefaultHttpContext();using var output=new MemoryStream();var original=new StreamResponseBodyFeature(output);context.Features.Set<IHttpResponseBodyFeature>(original);context.Response.Headers.CacheControl="public,max-age=60";
        using(var capture=BoundedResponseCapture.Install(context,1024))
        {
            context.Features.Get<IHttpResponseBodyFeature>()!.DisableBuffering();
            var bytes=new byte[512];Array.Fill<byte>(bytes,42);
            if(pipe){await context.Response.BodyWriter.WriteAsync(bytes);await context.Response.BodyWriter.FlushAsync();}else await context.Response.Body.WriteAsync(bytes);
            var entry=capture.TryComplete(new(true,TimeSpan.FromSeconds(60),0,"eligible"));Assert.NotNull(entry);Assert.Equal(bytes,entry.Body);
            if(pipe){await context.Response.BodyWriter.WriteAsync(new byte[1024]);await context.Response.BodyWriter.FlushAsync();}else await context.Response.Body.WriteAsync(new byte[1024]);
            Assert.Null(capture.TryComplete(new(true,TimeSpan.FromSeconds(60),0,"eligible")));Assert.Equal(1536,output.Length);
        }
        Assert.Same(original,context.Features.Get<IHttpResponseBodyFeature>());
    }
    [Fact]public async Task JsonRejectionCanWriteThroughCapturePipeWriter()
    {
        var context=new DefaultHttpContext();using var output=new MemoryStream();var original=new StreamResponseBodyFeature(output);context.Features.Set<IHttpResponseBodyFeature>(original);
        using(var capture=BoundedResponseCapture.Install(context,1024)){context.Response.StatusCode=503;await context.Response.WriteAsJsonAsync(new{code="circuit_open"});Assert.Null(capture.TryComplete(new(false,TimeSpan.Zero,0,"response_ineligible")));}
        Assert.Contains("circuit_open",System.Text.Encoding.UTF8.GetString(output.ToArray()));Assert.Same(original,context.Features.Get<IHttpResponseBodyFeature>());
    }

}
