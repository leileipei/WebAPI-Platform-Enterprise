using System.Text.Json;
using WebApi.Contracts.Common;
using WebApi.Contracts.Policies;
namespace WebApi.Domain.Policies;
public enum CircuitOutcome {Success,Failure,Neutral,Cancelled}
public enum CircuitStatus {Closed,Open,HalfOpen}
public sealed record CircuitAdmission(bool Allowed,long Epoch,bool Probe,int RetryAfterSeconds=0);
public sealed class CircuitBreakerState
{
    private sealed class Bucket {public long Second=-1,Samples,Failures;}
    private readonly object sync=new();private readonly CircuitBreakerConfiguration configuration;private readonly TimeProvider clock;private readonly long origin;private readonly Bucket[] buckets;
    private CircuitStatus status;private long epoch;private double openUntil;private int probes,successes;
    public CircuitBreakerState(CircuitBreakerConfiguration configuration,TimeProvider clock)
    {
        this.configuration=PolicyConfigurationValidator.ParseCircuit(JsonSerializer.Serialize(configuration,CanonicalJson.Options));this.clock=clock;origin=clock.GetTimestamp();buckets=Enumerable.Range(0,configuration.SamplingWindowMs/1000).Select(_=>new Bucket()).ToArray();
    }
    private double Now=>Math.Max(0,clock.GetElapsedTime(origin,clock.GetTimestamp()).TotalMilliseconds);
    public event Action<CircuitStatus,CircuitStatus>? Transitioned;
    public CircuitStatus Status {get {lock(sync) return status;}}
    public int BucketCapacity=>buckets.Length;
    public long SampleCount {get {lock(sync) {Prune((long)(Now/1000));return buckets.Sum(b=>b.Samples);}}}
    public CircuitAdmission TryEnter()
    {
        lock(sync) {
            var now=Now;
            if(status==CircuitStatus.Open) {
                if(now<openUntil) return new(false,epoch,false,Math.Max(1,(int)Math.Ceiling((openUntil-now)/1000)));
                probes=0;successes=0;Transition(CircuitStatus.HalfOpen);
            }
            if(status==CircuitStatus.HalfOpen) {if(probes>=configuration.HalfOpenMaxRequests) return new(false,epoch,false,1);probes++;return new(true,epoch,true);}
            return new(true,epoch,false);
        }
    }
    public bool CanContinue(CircuitAdmission admission)
    {lock(sync) return admission.Allowed&&!admission.Probe&&admission.Epoch==epoch&&status==CircuitStatus.Closed;}
    public void Complete(CircuitAdmission admission,CircuitOutcome outcome)
    {
        lock(sync) {
            if(!admission.Allowed||admission.Epoch!=epoch) return;
            if(admission.Probe&&status==CircuitStatus.HalfOpen) {
                probes=Math.Max(0,probes-1);
                if(outcome==CircuitOutcome.Failure) Open();
                else if(outcome==CircuitOutcome.Success&&++successes>=configuration.HalfOpenSuccesses) {probes=0;successes=0;Clear();Transition(CircuitStatus.Closed);}
                return;
            }
            if(status!=CircuitStatus.Closed||admission.Probe||outcome==CircuitOutcome.Cancelled) return;
            var second=(long)(Now/1000);Prune(second);var bucket=buckets[(int)(second%buckets.Length)];
            if(bucket.Second!=second) {bucket.Second=second;bucket.Samples=0;bucket.Failures=0;}
            bucket.Samples++;if(outcome==CircuitOutcome.Failure) bucket.Failures++;
            var samples=buckets.Sum(b=>b.Samples);var failures=buckets.Sum(b=>b.Failures);
            if(samples>=configuration.MinimumRequests&&failures/(double)samples>=configuration.FailureRatio) Open();
        }
    }
    private void Open() {openUntil=Now+configuration.OpenDurationMs;probes=0;successes=0;Transition(CircuitStatus.Open);}
    private void Transition(CircuitStatus next) {var prior=status;status=next;epoch++;try {Transitioned?.Invoke(prior,next);}catch { /* Observation cannot change admission state. */ }}
    private void Prune(long second) {foreach(var b in buckets) if(b.Second<second-buckets.Length+1) {b.Second=-1;b.Samples=0;b.Failures=0;}}
    private void Clear() {foreach(var b in buckets) {b.Second=-1;b.Samples=0;b.Failures=0;}}
}
