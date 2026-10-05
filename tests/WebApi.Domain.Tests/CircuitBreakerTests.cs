using WebApi.Contracts.Policies;
using WebApi.Domain.Policies;
using Xunit;
namespace WebApi.Domain.Tests;
public sealed class CircuitBreakerTests
{
    public sealed class Clock : TimeProvider {private long ms;public override long TimestampFrequency=>1000;public override long GetTimestamp()=>Interlocked.Read(ref ms);public void Advance(int milliseconds)=>Interlocked.Add(ref ms,milliseconds);}
    private static CircuitBreakerConfiguration Config()=>new(30000,20,0.5,30000,1,3,[500,503],true,true);
    private static void Open(CircuitBreakerState state) {for(var i=0;i<20;i++) {var admission=state.TryEnter();Assert.True(admission.Allowed);state.Complete(admission,i<10?CircuitOutcome.Failure:CircuitOutcome.Success);}Assert.False(state.TryEnter().Allowed);}
    [Fact] public void WindowAndMinimumBeforeRatioOpens()
    {var state=new CircuitBreakerState(Config(),new Clock());for(var i=0;i<19;i++) {var a=state.TryEnter();Assert.True(a.Allowed);state.Complete(a,i<10?CircuitOutcome.Failure:CircuitOutcome.Success);}var twentieth=state.TryEnter();Assert.True(twentieth.Allowed);state.Complete(twentieth,CircuitOutcome.Success);Assert.False(state.TryEnter().Allowed);}
    [Fact] public void OpenWaitThenLimitedHalfOpen()
    {var clock=new Clock();var state=new CircuitBreakerState(Config(),clock);Open(state);Assert.Equal(30,state.TryEnter().RetryAfterSeconds);clock.Advance(29999);Assert.False(state.TryEnter().Allowed);clock.Advance(1);Assert.True(state.TryEnter().Probe);Assert.False(state.TryEnter().Allowed);}
    [Fact] public void EnoughTwoHundredsCloses()
    {var clock=new Clock();var state=new CircuitBreakerState(Config(),clock);Open(state);clock.Advance(30000);for(var i=0;i<3;i++) {var a=state.TryEnter();Assert.True(a.Probe);state.Complete(a,CircuitOutcome.Success);}Assert.Equal(CircuitStatus.Closed,state.Status);Assert.False(state.TryEnter().Probe);}
    [Fact] public void LateSuccessCannotCloseReopenedEpoch()
    {var clock=new Clock();var state=new CircuitBreakerState(Config() with {HalfOpenMaxRequests=2},clock);Open(state);clock.Advance(30000);var old=state.TryEnter();var failure=state.TryEnter();state.Complete(failure,CircuitOutcome.Failure);state.Complete(old,CircuitOutcome.Success);Assert.Equal(CircuitStatus.Open,state.Status);Assert.False(state.TryEnter().Allowed);Assert.Equal(30,state.TryEnter().RetryAfterSeconds);}
    [Fact] public void CancelledProbeReleasesSlot()
    {var clock=new Clock();var state=new CircuitBreakerState(Config(),clock);Open(state);clock.Advance(30000);var a=state.TryEnter();state.Complete(a,CircuitOutcome.Cancelled);Assert.True(state.TryEnter().Probe);}
    [Fact] public void NeutralFourHundredDoesNotRecover()
    {var clock=new Clock();var state=new CircuitBreakerState(Config(),clock);Open(state);clock.Advance(30000);for(var i=0;i<20;i++) state.Complete(state.TryEnter(),CircuitOutcome.Neutral);Assert.Equal(CircuitStatus.HalfOpen,state.Status);Assert.True(state.TryEnter().Probe);}
    [Fact] public void OnlyOneWindowOfBucketsRetained()
    {var clock=new Clock();var state=new CircuitBreakerState(Config() with {SamplingWindowMs=300000,MinimumRequests=1000000},clock);for(var i=0;i<1000;i++) {state.Complete(state.TryEnter(),CircuitOutcome.Failure);clock.Advance(1000);}Assert.Equal(300,state.BucketCapacity);Assert.InRange(state.SampleCount,0,300);}
    [Fact] public void ExpiredSamplesCannotOpenNewWindow()
    {var clock=new Clock();var state=new CircuitBreakerState(Config(),clock);for(var i=0;i<19;i++) state.Complete(state.TryEnter(),CircuitOutcome.Failure);clock.Advance(30000);state.Complete(state.TryEnter(),CircuitOutcome.Failure);Assert.Equal(CircuitStatus.Closed,state.Status);Assert.Equal(1,state.SampleCount);}
    [Fact] public void HundredConcurrentAdmissionsRespectProbeLimit()
    {var clock=new Clock();var state=new CircuitBreakerState(Config() with {HalfOpenMaxRequests=3},clock);Open(state);clock.Advance(30000);var admissions=new System.Collections.Concurrent.ConcurrentBag<CircuitAdmission>();Parallel.For(0,100,_=>admissions.Add(state.TryEnter()));Assert.Equal(3,admissions.Count(a=>a.Allowed));foreach(var a in admissions.Where(a=>a.Allowed)) state.Complete(a,CircuitOutcome.Cancelled);Assert.True(state.TryEnter().Allowed);}
    [Fact] public void ClosedNeutralIsSampleAndCancellationIsNot()
    {var state=new CircuitBreakerState(Config(),new Clock());state.Complete(state.TryEnter(),CircuitOutcome.Neutral);state.Complete(state.TryEnter(),CircuitOutcome.Cancelled);Assert.Equal(1,state.SampleCount);}
    [Fact] public void TransitionEventsOnceAndObserverFailureCannotChangeAdmission()
    {var clock=new Clock();var state=new CircuitBreakerState(Config(),clock);var transitions=new List<CircuitStatus>();state.Transitioned+=(prior,next)=>{transitions.Add(next);throw new InvalidOperationException("observer failure");};Open(state);for(var i=0;i<10;i++)Assert.False(state.TryEnter().Allowed);clock.Advance(30000);for(var i=0;i<3;i++)state.Complete(state.TryEnter(),CircuitOutcome.Success);Assert.Equal(new[]{CircuitStatus.Open,CircuitStatus.HalfOpen,CircuitStatus.Closed},transitions);Assert.True(state.TryEnter().Allowed);}

}
