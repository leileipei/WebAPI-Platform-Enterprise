using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using StackExchange.Redis;

namespace WebApi.Infrastructure.Security;

public enum AuthenticationDiagnosticPhase
{
    ServerLogin, RateTotal, RateConnect, RateLua, RateDecode,
    AuditWrite, AuditValidate, AuditTransaction, AuditSave, AuditCommit,
    GateClaim, GateConfirm, GateRelease, GateConnect, GateLua
}
public enum AuthenticationDiagnosticOutcome { Started, Completed, Failed, Cancelled, NotReached }
public enum AuthenticationAuditKind { None, Success, InvalidCredentials, Throttled }
public enum AuthenticationDiagnosticError
{
    None, BudgetElapsed, RequestCancelled, RedisTimeout, RedisConnection, RedisServer, RedisOther,
    TimeoutOther, ResultCast, ResultOverflow, ResultShape, DbCheck, DbUnique, DbReference,
    DbDeadlock, DbSerialization, DbQueryCancelled, SecurityClockGuard, DbConnection, DbOther,
    AuditContract, AuditIo, Unexpected
}
public readonly record struct AuthenticationDiagnosticFailure(
    AuthenticationDiagnosticError Error, bool RequestCancelled = false, bool BudgetCancelled = false, string? SqlState = null);
public readonly record struct AuthenticationDiagnosticEvent(
    AuthenticationDiagnosticPhase Phase, AuthenticationDiagnosticOutcome Outcome,
    long StartedTimestamp, long FinishedTimestamp, AuthenticationDiagnosticFailure Failure = default,
    int? BudgetMs = null, bool? ConnectionWasCreated = null, long OperationOrdinal = 0,
    AuthenticationAuditKind AuditKind = AuthenticationAuditKind.None, long BudgetStartedTimestamp = 0);

public interface IAuthenticationDiagnosticSink
{
    bool Enabled { get; }
    void Record(in AuthenticationDiagnosticEvent value);
    void MarkIncomplete();
}
public sealed class NullAuthenticationDiagnosticSink : IAuthenticationDiagnosticSink
{
    public bool Enabled => false;
    public void Record(in AuthenticationDiagnosticEvent value) { }
    public void MarkIncomplete() { }
}

// Observation only: no exception object, command, identity, or arbitrary text reaches the sink.
public static class AuthenticationDiagnostics
{
    private static long operationOrdinal;
    public static AuthenticationDiagnosticScope? Start(IAuthenticationDiagnosticSink? sink,
        AuthenticationDiagnosticPhase phase, int? budgetMs = null, bool? connectionWasCreated = null,
        long operation = 0, AuthenticationAuditKind auditKind = AuthenticationAuditKind.None)
    {
        try
        {
            if (sink?.Enabled != true) return null;
            if (phase is AuthenticationDiagnosticPhase.GateClaim or AuthenticationDiagnosticPhase.GateConfirm or AuthenticationDiagnosticPhase.GateRelease)
                operation = Interlocked.Increment(ref operationOrdinal);
            return new(sink, phase, budgetMs, connectionWasCreated, operation, auditKind);
        }
        catch { MarkIncomplete(sink); return null; }
    }
    internal static void Record(IAuthenticationDiagnosticSink sink, in AuthenticationDiagnosticEvent value)
    {
        try { sink.Record(value); } catch { MarkIncomplete(sink); }
    }
    private static void MarkIncomplete(IAuthenticationDiagnosticSink? sink)
    {
        try { sink?.MarkIncomplete(); } catch { /* Never replace a business result. */ }
    }
    public static void NotReached(IAuthenticationDiagnosticSink? sink, AuthenticationDiagnosticPhase phase, long operation = 0, AuthenticationAuditKind auditKind = AuthenticationAuditKind.None)
    {
        try
        {
            if (sink?.Enabled == true) Record(sink, new(phase, AuthenticationDiagnosticOutcome.NotReached, 0, 0, OperationOrdinal: operation, AuditKind: auditKind));
        }
        catch { MarkIncomplete(sink); }
    }
    public static AuthenticationDiagnosticFailure Classify(Exception error, CancellationToken request = default, CancellationToken budget = default,
        AuthenticationDiagnosticPhase? phase = null)
    {
        var requestCancelled = request.IsCancellationRequested;
        var budgetCancelled = budget.IsCancellationRequested;
        if (requestCancelled) return new(AuthenticationDiagnosticError.RequestCancelled, true, budgetCancelled);
        if (error is OperationCanceledException && budgetCancelled) return new(AuthenticationDiagnosticError.BudgetElapsed, false, true);
        var cause = error is DbUpdateException { InnerException: PostgresException postgres } ? postgres : error;
        string? state = null;
        AuthenticationDiagnosticError kind;
        if (cause is PostgresException db)
        {
            kind = db.SqlState switch
            {
                "23514" when db.MessageText == "security_authoritative_fact_in_future" => AuthenticationDiagnosticError.SecurityClockGuard,
                "23514" => AuthenticationDiagnosticError.DbCheck,
                "23505" => AuthenticationDiagnosticError.DbUnique,
                "23503" => AuthenticationDiagnosticError.DbReference,
                "40P01" => AuthenticationDiagnosticError.DbDeadlock,
                "40001" => AuthenticationDiagnosticError.DbSerialization,
                "57014" => AuthenticationDiagnosticError.DbQueryCancelled,
                _ => AuthenticationDiagnosticError.DbOther
            };
            if (kind != AuthenticationDiagnosticError.DbOther) state = db.SqlState;
        }
        else kind = cause switch
        {
            RedisTimeoutException => AuthenticationDiagnosticError.RedisTimeout,
            RedisConnectionException => AuthenticationDiagnosticError.RedisConnection,
            RedisServerException => AuthenticationDiagnosticError.RedisServer,
            RedisException => AuthenticationDiagnosticError.RedisOther,
            TimeoutException => AuthenticationDiagnosticError.TimeoutOther,
            InvalidCastException => AuthenticationDiagnosticError.ResultCast,
            OverflowException => AuthenticationDiagnosticError.ResultOverflow,
            NpgsqlException => AuthenticationDiagnosticError.DbConnection,
            DbUpdateException => AuthenticationDiagnosticError.DbOther,
            ArgumentException when phase is AuthenticationDiagnosticPhase.AuditWrite or AuthenticationDiagnosticPhase.AuditValidate or AuthenticationDiagnosticPhase.AuditTransaction or AuthenticationDiagnosticPhase.AuditSave or AuthenticationDiagnosticPhase.AuditCommit => AuthenticationDiagnosticError.AuditContract,
            IOException when phase is AuthenticationDiagnosticPhase.AuditWrite or AuthenticationDiagnosticPhase.AuditValidate or AuthenticationDiagnosticPhase.AuditTransaction or AuthenticationDiagnosticPhase.AuditSave or AuthenticationDiagnosticPhase.AuditCommit => AuthenticationDiagnosticError.AuditIo,
            _ => AuthenticationDiagnosticError.Unexpected
        };
        return new(kind, requestCancelled, budgetCancelled, state);
    }
}

public sealed class AuthenticationDiagnosticScope : IDisposable
{
    private readonly IAuthenticationDiagnosticSink sink;
    private readonly AuthenticationDiagnosticEvent initial;
    private AuthenticationDiagnosticFailure failure;
    private AuthenticationDiagnosticOutcome outcome = AuthenticationDiagnosticOutcome.Completed;
    private bool failed;
    private bool disposed;
    private long budgetStarted;
    public long OperationOrdinal => initial.OperationOrdinal;
    public void BudgetStarted() => budgetStarted = Stopwatch.GetTimestamp();
    internal AuthenticationDiagnosticScope(IAuthenticationDiagnosticSink sink, AuthenticationDiagnosticPhase phase,
        int? budgetMs, bool? connectionWasCreated, long operation, AuthenticationAuditKind auditKind)
    {
        this.sink = sink;
        initial = new(phase, AuthenticationDiagnosticOutcome.Started, Stopwatch.GetTimestamp(), 0,
            BudgetMs: budgetMs, ConnectionWasCreated: connectionWasCreated, OperationOrdinal: operation, AuditKind: auditKind);
        if (phase == AuthenticationDiagnosticPhase.ServerLogin) AuthenticationDiagnostics.Record(sink, initial);
    }
    public void Fail(Exception error, CancellationToken request = default, CancellationToken budget = default)
    {
        if (failed) return; // A cleanup or outer mapped error must not erase the primary classification.
        try
        {
            failure = AuthenticationDiagnostics.Classify(error, request, budget, initial.Phase);
            outcome = error is OperationCanceledException ? AuthenticationDiagnosticOutcome.Cancelled : AuthenticationDiagnosticOutcome.Failed;
            failed = true;
        }
        catch { Mark(AuthenticationDiagnosticError.Unexpected); }
    }
    public void Mark(AuthenticationDiagnosticError error)
    {
        if (failed) return;
        failure = new(error); outcome = AuthenticationDiagnosticOutcome.Failed; failed = true;
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        AuthenticationDiagnostics.Record(sink, initial with
        { Outcome = outcome, FinishedTimestamp = Stopwatch.GetTimestamp(), Failure = failure, BudgetStartedTimestamp = budgetStarted });
    }
}
