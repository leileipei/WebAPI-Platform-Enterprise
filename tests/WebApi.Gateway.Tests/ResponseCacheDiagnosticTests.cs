using System.Collections.Concurrent;
using WebApi.Gateway.Policies;
using Xunit;

namespace WebApi.Gateway.Tests;

public sealed class ResponseCacheDiagnosticTests
{
    private sealed class Sink:IResponseCacheDiagnosticSink
    {
        internal ConcurrentQueue<ResponseCacheDiagnosticEvent> Events=[];
        public void Record(in ResponseCacheDiagnosticEvent value)=>Events.Enqueue(value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TaskReturnDistinguishesSynchronousDispatchFromPendingCompletion(bool alreadyCompleted)
    {
        var sink=new Sink();var operation=new ResponseCacheDiagnosticOperation(sink,"put",default,default);
        var completion=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        if(alreadyCompleted)completion.SetResult(7);
        var pending=operation.Observe(ResponseCachePhase.Command,()=>completion.Task);
        var returned=Assert.Single(sink.Events,e=>e.Outcome==ResponseCacheOutcome.TaskReturned);
        Assert.Equal(ResponseCachePhase.Command,returned.Phase);Assert.Equal(alreadyCompleted,returned.TaskIsCompleted);
        if(!alreadyCompleted)
        {
            Assert.False(pending.IsCompleted);
            Assert.DoesNotContain(sink.Events,e=>e.Outcome==ResponseCacheOutcome.Completed);
            completion.SetResult(7);
        }
        Assert.Equal(7,await pending);operation.Finish();
        Assert.Equal(new[]{ResponseCacheOutcome.Started,ResponseCacheOutcome.TaskReturned,ResponseCacheOutcome.Completed,ResponseCacheOutcome.Completed},sink.Events.Select(e=>e.Outcome));
        Assert.All(sink.Events,e=>Assert.Equal(returned.OperationOrdinal,e.OperationOrdinal));
        Assert.Equal(ResponseCachePhase.Decision,sink.Events.Last().Phase);
    }

    [Fact]
    public async Task SynchronousDispatchFailureDoesNotReportReturnedTask()
    {
        var sink=new Sink();var operation=new ResponseCacheDiagnosticOperation(sink,"put",default,default);
        var expected=new IOException("private-command-details");
        var error=await Assert.ThrowsAsync<IOException>(()=>operation.Observe<int>(ResponseCachePhase.Command,()=>throw expected));
        Assert.Same(expected,error);operation.Finish(error);
        Assert.DoesNotContain(sink.Events,e=>e.Outcome==ResponseCacheOutcome.TaskReturned);
        Assert.Equal(new[]{ResponseCacheOutcome.Started,ResponseCacheOutcome.Io,ResponseCacheOutcome.Io},sink.Events.Select(e=>e.Outcome));
        Assert.DoesNotContain("private-command-details",System.Text.Json.JsonSerializer.Serialize(sink.Events));
    }
}
