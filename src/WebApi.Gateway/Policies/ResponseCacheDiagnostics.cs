using System.Diagnostics;
using System.Net.Sockets;
using StackExchange.Redis;

namespace WebApi.Gateway.Policies;

public enum ResponseCachePhase { Decision, Connect, Command }
public enum ResponseCacheOutcome { Started, Completed, BudgetElapsed, RequestCancelled, RedisConnection, RedisTimeout, RedisServer, RedisOther, Timeout, Io, InvalidResult, Unexpected }
public readonly record struct ResponseCacheDiagnosticEvent(long OperationOrdinal,string Operation,ResponseCachePhase Phase,
    ResponseCacheOutcome Outcome,double ElapsedMs,ResponseCachePhase? WaitingFor=null,bool? ConnectionIsConnected=null);
public interface IResponseCacheDiagnosticSink { void Record(in ResponseCacheDiagnosticEvent value); }

// Optional observation only: no keys, payloads, connection strings or exception messages.
internal sealed class ResponseCacheDiagnosticOperation(IResponseCacheDiagnosticSink sink,string operation,CancellationToken request,CancellationToken budget)
{
    private static long ordinal;
    private readonly long id=Interlocked.Increment(ref ordinal);
    private readonly long started=Stopwatch.GetTimestamp();
    private int waiting=-1;
    internal async Task<T> Observe<T>(ResponseCachePhase phase,Func<Task<T>> action)
    {
        var began=Stopwatch.GetTimestamp();Volatile.Write(ref waiting,(int)phase);
        Record(phase,ResponseCacheOutcome.Started,began);
        try {var result=await action();Record(phase,ResponseCacheOutcome.Completed,began,connectionIsConnected:ConnectionState(result));return result;}
        catch(Exception error){Record(phase,Classify(error),began);throw;}
        finally {Volatile.Write(ref waiting,-1);}
    }
    internal void Finish(Exception? error=null)
    {
        var active=Volatile.Read(ref waiting);
        Record(ResponseCachePhase.Decision,error is null?ResponseCacheOutcome.Completed:Classify(error),started,
            active<0?null:(ResponseCachePhase)active);
    }
    private ResponseCacheOutcome Classify(Exception error)
    {
        if(request.IsCancellationRequested)return ResponseCacheOutcome.RequestCancelled;
        if(error is OperationCanceledException && budget.IsCancellationRequested)return ResponseCacheOutcome.BudgetElapsed;
        return error switch {
            RedisConnectionException=>ResponseCacheOutcome.RedisConnection,
            RedisTimeoutException=>ResponseCacheOutcome.RedisTimeout,
            RedisServerException=>ResponseCacheOutcome.RedisServer,
            RedisException=>ResponseCacheOutcome.RedisOther,
            TimeoutException=>ResponseCacheOutcome.Timeout,
            SocketException or IOException=>ResponseCacheOutcome.Io,
            InvalidOperationException or InvalidCastException=>ResponseCacheOutcome.InvalidResult,
            _=>ResponseCacheOutcome.Unexpected
        };
    }
    private static bool? ConnectionState<T>(T value)
    {
        try {return value is ConnectionMultiplexer connection?connection.IsConnected:null;}
        catch {return null;}
    }
    private void Record(ResponseCachePhase phase,ResponseCacheOutcome outcome,long began,ResponseCachePhase? waitingFor=null,bool? connectionIsConnected=null)
    {
        try {sink.Record(new(id,operation,phase,outcome,Math.Round(Stopwatch.GetElapsedTime(began).TotalMilliseconds,3),waitingFor,connectionIsConnected));}
        catch { /* Diagnostic failures cannot replace a cache result or cancellation. */ }
    }
}
