using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using WebApi.Infrastructure.Security;

namespace WebApi.Integration.Tests.Support;

public sealed record LoginFailureResponse(int Status, string? Code, string? TraceId, int? RetryAfterSeconds,
    string RetryAfterState, string BodyState, string TraceState);

// Only the explicitly enabled fixture installs this sink. No logging provider or exporter is enabled.
public sealed class LoginDiagnosticCollector(IHttpContextAccessor accessor) : IAuthenticationDiagnosticSink
{
    private sealed class RequestRecord(long origin)
    {
        public long Origin { get; } = origin;
        public List<AuthenticationDiagnosticEvent> Events { get; } = [];
        public bool Complete;
        public bool Lost;
    }
    private readonly object sync = new();
    private readonly Dictionary<string, RequestRecord> requests = new(StringComparer.Ordinal);
    private bool unassociated;
    private int outputBytes;
    private readonly HashSet<int> outputOrdinals = [];
    public bool Enabled => true;

    public void MarkIncomplete()
    {
        lock(sync)
        {
            var trace=accessor.HttpContext?.TraceIdentifier;
            if(trace is not null && requests.TryGetValue(trace,out var record))record.Lost=true;
            else unassociated=true;
        }
    }

    public void Record(in AuthenticationDiagnosticEvent value)
    {
        var context = accessor.HttpContext;
        if (context is null || context.Request.Path != "/api/v1/auth/login") return;
        lock (sync)
        {
            var trace = context.TraceIdentifier;
            if (!ValidTrace(trace)) { unassociated = true; return; }
            if (!requests.TryGetValue(trace, out var record))
            {
                if (value.Phase != AuthenticationDiagnosticPhase.ServerLogin || value.Outcome != AuthenticationDiagnosticOutcome.Started || requests.Count >= 4)
                { unassociated = true; return; }
                requests.Add(trace, record = new(value.StartedTimestamp));
            }
            if (value.Phase == AuthenticationDiagnosticPhase.ServerLogin && value.Outcome == AuthenticationDiagnosticOutcome.Started) return;
            if (record.Events.Count >= 32) { record.Lost = true; return; }
            record.Events.Add(value);
            if (value.Phase == AuthenticationDiagnosticPhase.ServerLogin) record.Complete = true;
        }
    }

    public async Task<string?> BuildFailureAsync(HttpResponseMessage response, int requestOrdinal,
        double csrfMs, double loginMs, double totalMs)
    {
        if (response.StatusCode != HttpStatusCode.ServiceUnavailable || requestOrdinal is < 1 or > 4) return null;
        try
        {
            lock(sync)
            {
                if(outputOrdinals.Count>=4 || !outputOrdinals.Add(requestOrdinal))return null;
            }
            var parsing = Stopwatch.GetTimestamp();
            var summary = await ReadResponseAsync(response);
            var parsingMs = Milliseconds(parsing, Stopwatch.GetTimestamp());
            lock (sync)
            {
                // A response may be output once; callers do not retry if this cap is reached.
                if (outputBytes >= 32768) return null;
                RequestRecord? record = null;
                if (summary.TraceId is not null) requests.TryGetValue(summary.TraceId, out record);
                var correlation = summary.TraceState == "invalid" ? "invalid_trace" : summary.TraceId is null ? "missing_trace" : record is null ? "no_server_record" : "matched";
                var complete = record is { Complete: true, Lost: false } && !unassociated && summary.BodyState == "parsed" && summary.Code is not null and not "unrecognized_code";
                var events = record?.Events.ToArray() ?? [];
                complete &= CompletePath(events,summary.Code);
                // Never echo an otherwise well-formed identifier that we cannot attest locally.
                var safeSummary=record is null?summary with { TraceId=null }:summary;
                var keep = events.Length;
                while (true)
                {
                    var output = JsonSerializer.Serialize(new
                    {
                        schemaVersion = 1, requestOrdinal, response = safeSummary, correlation,
                        diagnosticStatus = complete && keep == events.Length ? "complete" : "incomplete",
                        client = new { csrfMs = Finite(csrfMs), loginSendMs = Finite(loginMs), totalMs = Finite(totalMs), diagnosticParseMs = parsingMs },
                        missingPhasePolicy = "unknown_unless_explicit_not_reached",
                        events = events.Take(keep).Select(e => Project(e, record!))
                    });
                    var bytes = Encoding.UTF8.GetByteCount(output);
                    if (bytes <= 32768 - outputBytes) { outputBytes += bytes; return output; }
                    if (keep == 0) return null;
                    keep--; // Drop whole trailing events, never bytes from a JSON document.
                }
            }
        }
        catch { return null; }
    }

    private static object Project(AuthenticationDiagnosticEvent value, RequestRecord record)
    {
        var origin = record.Origin;
        if (value.OperationOrdinal != 0)
        {
            var parent = record.Events.FirstOrDefault(e => e.OperationOrdinal == value.OperationOrdinal &&
                e.Phase is AuthenticationDiagnosticPhase.GateClaim or AuthenticationDiagnosticPhase.GateConfirm or AuthenticationDiagnosticPhase.GateRelease);
            if (parent.StartedTimestamp > 0) origin = parent.StartedTimestamp;
        }
        else if (value.Phase is AuthenticationDiagnosticPhase.RateConnect or AuthenticationDiagnosticPhase.RateLua or AuthenticationDiagnosticPhase.RateDecode)
        {
            var parent = record.Events.FirstOrDefault(e => e.Phase == AuthenticationDiagnosticPhase.RateTotal);
            if (parent.StartedTimestamp > 0) origin = parent.StartedTimestamp;
        }
        return new
        {
            phase = EnumName(value.Phase), outcome = EnumName(value.Outcome), error = EnumName(value.Failure.Error),
            auditKind = EnumName(value.AuditKind), value.OperationOrdinal,
            elapsedMs = value.StartedTimestamp > 0 && value.FinishedTimestamp >= value.StartedTimestamp ? (double?)Milliseconds(value.StartedTimestamp, value.FinishedTimestamp) : null,
            offsetMs = value.StartedTimestamp >= origin && origin > 0 ? (double?)Milliseconds(origin, value.StartedTimestamp) : null,
            budgetMs = value.BudgetMs == 1000 ? (int?)1000 : null, value.ConnectionWasCreated,
            budgetStartOffsetMs = value.BudgetStartedTimestamp >= value.StartedTimestamp && value.StartedTimestamp > 0 ?
                (double?)Milliseconds(value.StartedTimestamp, value.BudgetStartedTimestamp) : null,
            value.Failure.RequestCancelled, value.Failure.BudgetCancelled,
            sqlState = value.Failure.SqlState is "23514" or "23505" or "23503" or "40P01" or "40001" or "57014" ? value.Failure.SqlState : null
        };
    }

    private static bool CompletePath(AuthenticationDiagnosticEvent[] events,string? code)
    {
        bool One(AuthenticationDiagnosticPhase phase,out AuthenticationDiagnosticEvent value,long operation=0)
        {
            var matches=events.Where(e=>e.Phase==phase && e.OperationOrdinal==operation).Take(2).ToArray();
            value=matches.Length==1?matches[0]:default;
            return matches.Length==1 && value.Outcome!=AuthenticationDiagnosticOutcome.Started;
        }
        if(!One(AuthenticationDiagnosticPhase.RateTotal,out var total) ||
            !One(AuthenticationDiagnosticPhase.RateConnect,out var connect) ||
            !One(AuthenticationDiagnosticPhase.RateLua,out var lua) ||
            !One(AuthenticationDiagnosticPhase.RateDecode,out var decode))return false;
        if(connect.Outcome==AuthenticationDiagnosticOutcome.NotReached)return false;
        if((connect.Outcome==AuthenticationDiagnosticOutcome.Completed)==(lua.Outcome==AuthenticationDiagnosticOutcome.NotReached))return false;
        if((lua.Outcome==AuthenticationDiagnosticOutcome.Completed)==(decode.Outcome==AuthenticationDiagnosticOutcome.NotReached))return false;
        if(code=="login_protection_unavailable" && total.Outcome!=AuthenticationDiagnosticOutcome.Failed &&
            !(total.Outcome==AuthenticationDiagnosticOutcome.Cancelled &&
              total.Failure.Error==AuthenticationDiagnosticError.BudgetElapsed &&
              total.Failure.BudgetCancelled && !total.Failure.RequestCancelled))return false;
        if(code=="authentication_audit_unavailable" && total.Outcome!=AuthenticationDiagnosticOutcome.Completed)return false;
        var gates=events.Where(e=>e.Phase is AuthenticationDiagnosticPhase.GateClaim or AuthenticationDiagnosticPhase.GateConfirm or AuthenticationDiagnosticPhase.GateRelease).ToArray();
        foreach(var gate in gates)
        {
            if(gate.OperationOrdinal<=0 || !One(AuthenticationDiagnosticPhase.GateConnect,out var gateConnect,gate.OperationOrdinal) ||
                !One(AuthenticationDiagnosticPhase.GateLua,out var gateLua,gate.OperationOrdinal))return false;
            if(gateConnect.Outcome==AuthenticationDiagnosticOutcome.NotReached ||
                (gateConnect.Outcome==AuthenticationDiagnosticOutcome.Completed)==(gateLua.Outcome==AuthenticationDiagnosticOutcome.NotReached))return false;
        }
        if(events.Any(e=>(e.Phase is AuthenticationDiagnosticPhase.GateConnect or AuthenticationDiagnosticPhase.GateLua) &&
            !gates.Any(g=>g.OperationOrdinal==e.OperationOrdinal)))return false;
        static bool Failed(AuthenticationDiagnosticEvent e)=>e.Outcome is AuthenticationDiagnosticOutcome.Failed or AuthenticationDiagnosticOutcome.Cancelled;
        var writes=events.Where(e=>e.Phase==AuthenticationDiagnosticPhase.AuditWrite).ToArray();
        var auditFailure=false;
        if(writes.GroupBy(e=>e.AuditKind).Any(g=>g.Count()!=1))return false;
        foreach(var write in writes)
        {
            bool Phase(AuthenticationDiagnosticPhase phase,out AuthenticationDiagnosticEvent value)
            {
                var matches=events.Where(e=>e.Phase==phase && e.AuditKind==write.AuditKind).Take(2).ToArray();
                value=matches.Length==1?matches[0]:default;
                return matches.Length==1 && value.Outcome!=AuthenticationDiagnosticOutcome.Started;
            }
            bool Present(AuthenticationDiagnosticPhase phase)=>events.Any(e=>e.Phase==phase && e.AuditKind==write.AuditKind);
            if(!Phase(AuthenticationDiagnosticPhase.AuditValidate,out var validation))return false;
            if(Failed(validation))
            {
                if(!Failed(write) || Present(AuthenticationDiagnosticPhase.AuditTransaction) || Present(AuthenticationDiagnosticPhase.AuditSave) || Present(AuthenticationDiagnosticPhase.AuditCommit))return false;
                auditFailure=true;continue;
            }
            if(validation.Outcome!=AuthenticationDiagnosticOutcome.Completed || !Phase(AuthenticationDiagnosticPhase.AuditTransaction,out var transaction))return false;
            if(Failed(transaction))
            {
                if(!Failed(write) || Present(AuthenticationDiagnosticPhase.AuditSave) || Present(AuthenticationDiagnosticPhase.AuditCommit))return false;
                auditFailure=true;continue;
            }
            if(transaction.Outcome!=AuthenticationDiagnosticOutcome.Completed || !Phase(AuthenticationDiagnosticPhase.AuditSave,out var save))return false;
            if(Failed(save))
            {
                if(!Failed(write) || Present(AuthenticationDiagnosticPhase.AuditCommit))return false;
                auditFailure=true;continue;
            }
            if(save.Outcome!=AuthenticationDiagnosticOutcome.Completed &&
                !(write.AuditKind==AuthenticationAuditKind.Throttled && save.Outcome==AuthenticationDiagnosticOutcome.NotReached))return false;
            if(!Phase(AuthenticationDiagnosticPhase.AuditCommit,out var commit))return false;
            if(Failed(commit))
            {
                if(!Failed(write) && !(write.AuditKind==AuthenticationAuditKind.Success && write.Outcome==AuthenticationDiagnosticOutcome.Completed))return false;
                auditFailure=true;
            }
            else if(commit.Outcome!=AuthenticationDiagnosticOutcome.Completed || write.Outcome!=AuthenticationDiagnosticOutcome.Completed)return false;
        }
        return code!="authentication_audit_unavailable" || auditFailure || gates.Any(Failed);
    }

    public static async Task<LoginFailureResponse> ReadResponseAsync(HttpResponseMessage response)
    {
        try { return await ReadResponseCoreAsync(response); }
        catch { return new((int)response.StatusCode,null,null,null,"invalid","read_error","missing"); }
    }

    private static async Task<LoginFailureResponse> ReadResponseCoreAsync(HttpResponseMessage response)
    {
        var retryState = "missing"; int? retry = null;
        if (response.Headers.TryGetValues("Retry-After", out var raw))
        {
            var values = raw.Take(2).ToArray();
            retryState = "invalid";
            if (values.Length == 1 && int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds is >= 0 and <= 3600)
            { retry = seconds; retryState = "parsed"; }
        }
        LoginFailureResponse Empty(string state) => new((int)response.StatusCode, null, null, retry, retryState, state, "missing");
        var media = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(media, "application/problem+json", StringComparison.OrdinalIgnoreCase) && !string.Equals(media, "application/json", StringComparison.OrdinalIgnoreCase))
            return Empty("unsupported_content_type");
        Stream stream;
        try { stream = await response.Content.ReadAsStreamAsync(); }
        catch { return Empty("read_error"); }
        if (!stream.CanRead || !stream.CanSeek) return Empty("not_read");
        var buffer = new byte[4097]; var length = 0; var restored = false; var read = false; long position;
        try { position = stream.Position; } catch { return Empty("read_error"); }
        try
        {
            stream.Position = 0;
            while (length < buffer.Length)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(length));
                if (count == 0) break;
                length += count;
            }
            read = true;
        }
        catch { }
        finally { try { stream.Position = position; restored = true; } catch { } }
        try
        {
            if (!read || !restored) return Empty("read_error");
            if (length == 0) return Empty("empty");
            if (length > 4096) return Empty("too_large");
            using var document = JsonDocument.Parse(buffer.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return Empty("invalid_json");
            string? code = null; string? trace = null; var codeSeen = false; var traceSeen = false; var traceState = "missing";
            foreach (var field in document.RootElement.EnumerateObject())
            {
                if (field.NameEquals("code"))
                {
                    if (codeSeen) return Empty("invalid_json");
                    codeSeen = true;
                    var text = field.Value.ValueKind == JsonValueKind.String ? field.Value.GetString() : null;
                    code = text is "login_protection_unavailable" or "authentication_audit_unavailable" or "security_clock_unavailable" ? text : "unrecognized_code";
                }
                else if (field.NameEquals("traceId"))
                {
                    if (traceSeen) return Empty("invalid_json");
                    traceSeen = true;
                    var text = field.Value.ValueKind == JsonValueKind.String ? field.Value.GetString() : null;
                    traceState = ValidTrace(text) ? "valid" : "invalid";
                    if (traceState == "valid") trace = text;
                }
            }
            return new((int)response.StatusCode, code, trace, retry, retryState, "parsed", traceState);
        }
        catch (JsonException) { return Empty("invalid_json"); }
        finally { Array.Clear(buffer); }
    }

    private static bool ValidTrace(string? text) => text is { Length: >= 1 and <= 128 } && text.All(c => char.IsAsciiLetterOrDigit(c) || c is ':' or '_' or '-');
    private static string EnumName<T>(T value) where T : struct, Enum => Enum.IsDefined(value) ? value.ToString() : "Unknown";
    private static double? Finite(double value) => double.IsFinite(value) && value >= 0 ? Math.Round(value, 3) : null;
    private static double Milliseconds(long start, long end) => Math.Round(Stopwatch.GetElapsedTime(start, end).TotalMilliseconds, 3);
}
