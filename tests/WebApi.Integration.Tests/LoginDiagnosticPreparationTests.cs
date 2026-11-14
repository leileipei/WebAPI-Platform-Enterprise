using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebApi.Infrastructure.Security;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

// Prepared for a separately authorized offline run; these tests create no hosts or databases.
public sealed class LoginDiagnosticPreparationTests
{
    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.ServiceUnavailable)
    { Content = new StringContent(body, Encoding.UTF8, "application/problem+json") };

    [Theory]
    [InlineData("login_protection_unavailable")]
    [InlineData("authentication_audit_unavailable")]
    [InlineData("security_clock_unavailable")]
    public async Task ResponseProjectionPreservesCodeAndReadableBody(string code)
    {
        var body = JsonSerializer.Serialize(new { code, traceId = "0HNABC:0001", detail = "CANARY-SECRET" });
        using var response = Response(body);
        var value = await LoginDiagnosticCollector.ReadResponseAsync(response);
        Assert.Equal(code, value.Code);
        Assert.Equal("0HNABC:0001", value.TraceId);
        Assert.Equal(body, await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain("CANARY", JsonSerializer.Serialize(value));
    }

    [Fact]
    public async Task UnknownCodesAndInvalidTraceAreNotEchoed()
    {
        using var response = Response("{\"code\":\"CANARY-CODE\",\"traceId\":\"CANARY TRACE\"}");
        var value = await LoginDiagnosticCollector.ReadResponseAsync(response);
        Assert.Equal("unrecognized_code", value.Code);
        Assert.Null(value.TraceId);
        Assert.DoesNotContain("CANARY", JsonSerializer.Serialize(value));
    }

    [Theory]
    [InlineData("{\"code\":\"a\",\"code\":\"b\"}", "invalid_json")]
    [InlineData("{", "invalid_json")]
    [InlineData("", "empty")]
    public async Task MalformedResponsesDoNotReplaceTheOriginal503(string body, string state)
    {
        using var response = Response(body);
        var value = await LoginDiagnosticCollector.ReadResponseAsync(response);
        Assert.Equal(state, value.BodyState);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Throws<HttpRequestException>(() => response.EnsureSuccessStatusCode());
        Assert.Equal(body, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task DiagnosticReadIsBoundedAndBodyIsPreserved()
    {
        var body = new string('x', 4097);
        using var response = Response(body);
        Assert.Equal("too_large", (await LoginDiagnosticCollector.ReadResponseAsync(response)).BodyState);
        Assert.Equal(body, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("0", 0, "parsed")]
    [InlineData("3600", 3600, "parsed")]
    [InlineData("3601", null, "invalid")]
    [InlineData("CANARY-HEADER", null, "invalid")]
    public async Task RetryAfterUsesOnlyBoundedIntegerSeconds(string raw, int? expected, string state)
    {
        using var response = Response("{}");
        response.Headers.TryAddWithoutValidation("Retry-After", raw);
        var value = await LoginDiagnosticCollector.ReadResponseAsync(response);
        Assert.Equal(expected, value.RetryAfterSeconds);
        Assert.Equal(state, value.RetryAfterState);
        Assert.DoesNotContain("CANARY", JsonSerializer.Serialize(value));
    }

    [Fact]
    public void CancellationClassificationKeepsBothObservations()
    {
        using var request = new CancellationTokenSource();
        using var budget = new CancellationTokenSource();
        request.Cancel(); budget.Cancel();
        var value = AuthenticationDiagnostics.Classify(new OperationCanceledException(), request.Token, budget.Token);
        Assert.Equal(AuthenticationDiagnosticError.RequestCancelled, value.Error);
        Assert.True(value.RequestCancelled); Assert.True(value.BudgetCancelled);
    }

    [Fact]
    public void UnknownExceptionMessagesNeverBecomeEvents()
    {
        var value = AuthenticationDiagnostics.Classify(new Exception("CANARY-SECRET"));
        Assert.Equal(AuthenticationDiagnosticError.Unexpected, value.Error);
        Assert.DoesNotContain("CANARY", JsonSerializer.Serialize(value));
    }

    [Fact]
    public void DisabledSinkDoesNotCreateAScope()
        => Assert.Null(AuthenticationDiagnostics.Start(new NullAuthenticationDiagnosticSink(), AuthenticationDiagnosticPhase.ServerLogin));

    [Theory]
    [InlineData("23514", "ordinary check", AuthenticationDiagnosticError.DbCheck, "23514")]
    [InlineData("23514", "security_authoritative_fact_in_future", AuthenticationDiagnosticError.SecurityClockGuard, "23514")]
    [InlineData("23505", "CANARY-SECRET", AuthenticationDiagnosticError.DbUnique, "23505")]
    [InlineData("99999", "CANARY-SECRET", AuthenticationDiagnosticError.DbOther, null)]
    public void DatabaseProjectionOnlyUsesTheFixedSqlStateAllowlist(string state, string message, AuthenticationDiagnosticError expected, string? expectedState)
    {
        var error = new DbUpdateException("CANARY-WRAPPER", new PostgresException(message, "ERROR", "ERROR", state));
        var value = AuthenticationDiagnostics.Classify(error);
        Assert.Equal(expected, value.Error); Assert.Equal(expectedState, value.SqlState);
        Assert.DoesNotContain("CANARY", JsonSerializer.Serialize(value));
    }

    [Fact]
    public void BudgetCancellationDoesNotMasqueradeAsRequestCancellation()
    {
        using var budget = new CancellationTokenSource(); budget.Cancel();
        var value = AuthenticationDiagnostics.Classify(new OperationCanceledException(), budget: budget.Token);
        Assert.Equal(AuthenticationDiagnosticError.BudgetElapsed, value.Error);
        Assert.False(value.RequestCancelled); Assert.True(value.BudgetCancelled);
    }

    [Fact]
    public void ThrowingDiagnosticSinkCannotEscapeIntoBusinessCode()
    {
        using var scope = AuthenticationDiagnostics.Start(new ThrowingSink(), AuthenticationDiagnosticPhase.RateTotal);
        scope?.Fail(new IOException("CANARY-SECRET"));
    }

    [Fact]
    public async Task CollectorDoesNotUseAnotherRequestsLastFailure()
    {
        var context = new DefaultHttpContext { TraceIdentifier = "request-A" };
        context.Request.Path = "/api/v1/auth/login";
        var collector = new LoginDiagnosticCollector(new HttpContextAccessor { HttpContext = context });
        using (var scope = AuthenticationDiagnostics.Start(collector, AuthenticationDiagnosticPhase.ServerLogin))
            scope?.Fail(new IOException("CANARY-SECRET"));
        using var response = Response("{\"code\":\"authentication_audit_unavailable\",\"traceId\":\"request-B\"}");
        var output = await collector.BuildFailureAsync(response, 1, 0, 0, 0);
        Assert.NotNull(output);
        using var doc = JsonDocument.Parse(output!);
        Assert.Equal("no_server_record", doc.RootElement.GetProperty("correlation").GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("response").GetProperty("TraceId").ValueKind);
        Assert.Empty(doc.RootElement.GetProperty("events").EnumerateArray());
        Assert.DoesNotContain("CANARY", output!);
    }

    [Fact]
    public async Task AFailureResponseIsProjectedAtMostOncePerOrdinal()
    {
        var collector = new LoginDiagnosticCollector(new HttpContextAccessor());
        using var response = Response("{}");
        Assert.NotNull(await collector.BuildFailureAsync(response, 1, 0, 0, 0));
        Assert.Null(await collector.BuildFailureAsync(response, 1, 0, 0, 0));
    }

    [Fact]
    public async Task EventOverflowRetainsThe503AndProducesCompleteBoundedJson()
    {
        var context = new DefaultHttpContext { TraceIdentifier = "request-A" };
        context.Request.Path = "/api/v1/auth/login";
        var collector = new LoginDiagnosticCollector(new HttpContextAccessor { HttpContext = context });
        using (AuthenticationDiagnostics.Start(collector, AuthenticationDiagnosticPhase.ServerLogin))
            for (var i = 0; i < 40; i++)
                using (AuthenticationDiagnostics.Start(collector, AuthenticationDiagnosticPhase.AuditSave)) { }
        using var response = Response("{\"code\":\"authentication_audit_unavailable\",\"traceId\":\"request-A\"}");
        var output = await collector.BuildFailureAsync(response, 1, 0, 0, 0);
        Assert.NotNull(output); Assert.True(Encoding.UTF8.GetByteCount(output!) <= 32768);
        using var doc = JsonDocument.Parse(output!);
        Assert.Equal("incomplete", doc.RootElement.GetProperty("diagnosticStatus").GetString());
        Assert.Equal(503, doc.RootElement.GetProperty("response").GetProperty("Status").GetInt32());
        Assert.True(doc.RootElement.GetProperty("events").GetArrayLength() <= 32);
    }

    [Fact]
    public async Task MissingIntermediateRateEventsCannotBeReportedAsComplete()
    {
        var context=new DefaultHttpContext { TraceIdentifier="request-A" };context.Request.Path="/api/v1/auth/login";
        var collector=new LoginDiagnosticCollector(new HttpContextAccessor { HttpContext=context });
        using(AuthenticationDiagnostics.Start(collector,AuthenticationDiagnosticPhase.ServerLogin))
        using(AuthenticationDiagnostics.Start(collector,AuthenticationDiagnosticPhase.RateTotal))
        using(AuthenticationDiagnostics.Start(collector,AuthenticationDiagnosticPhase.RateConnect)) { }
        using var response=Response("{\"code\":\"login_protection_unavailable\",\"traceId\":\"request-A\"}");
        var output=await collector.BuildFailureAsync(response,1,0,0,0);
        using var document=JsonDocument.Parse(output!);
        Assert.Equal("incomplete",document.RootElement.GetProperty("diagnosticStatus").GetString());
    }

    [Fact]
    public async Task IntermittentDeliveryFailureRemainsIncompleteWhenLaterEventsRecover()
    {
        var context=new DefaultHttpContext { TraceIdentifier="request-A" };context.Request.Path="/api/v1/auth/login";
        var collector=new LoginDiagnosticCollector(new HttpContextAccessor { HttpContext=context });
        var sink=new IntermittentSink(collector);
        using(AuthenticationDiagnostics.Start(sink,AuthenticationDiagnosticPhase.ServerLogin))
        {
            using(AuthenticationDiagnostics.Start(sink,AuthenticationDiagnosticPhase.AuditValidate)) { }
            using var total=AuthenticationDiagnostics.Start(sink,AuthenticationDiagnosticPhase.RateTotal);
            using(var connect=AuthenticationDiagnostics.Start(sink,AuthenticationDiagnosticPhase.RateConnect))
                connect?.Fail(new TimeoutException());
            AuthenticationDiagnostics.NotReached(sink,AuthenticationDiagnosticPhase.RateLua);
            AuthenticationDiagnostics.NotReached(sink,AuthenticationDiagnosticPhase.RateDecode);
            total?.Fail(new TimeoutException());
        }
        using var response=Response("{\"code\":\"login_protection_unavailable\",\"traceId\":\"request-A\"}");
        var output=await collector.BuildFailureAsync(response,1,0,0,0);
        using var document=JsonDocument.Parse(output!);
        Assert.Equal("incomplete",document.RootElement.GetProperty("diagnosticStatus").GetString());
    }

    private sealed class IntermittentSink(LoginDiagnosticCollector collector):IAuthenticationDiagnosticSink
    {
        public bool Enabled=>true;
        public void Record(in AuthenticationDiagnosticEvent value)
        {
            if(value.Phase==AuthenticationDiagnosticPhase.AuditValidate)throw new IOException("CANARY-DELIVERY");
            collector.Record(value);
        }
        public void MarkIncomplete()=>collector.MarkIncomplete();
    }

    [Fact]
    public async Task MissingGateChildrenRemainIncompleteAfterACompleteRatePath()
    {
        var context=new DefaultHttpContext { TraceIdentifier="request-A" };context.Request.Path="/api/v1/auth/login";
        var collector=new LoginDiagnosticCollector(new HttpContextAccessor { HttpContext=context });
        using(AuthenticationDiagnostics.Start(collector,AuthenticationDiagnosticPhase.ServerLogin))
        {
            using(AuthenticationDiagnostics.Start(collector,AuthenticationDiagnosticPhase.RateTotal))
            {
                using(AuthenticationDiagnostics.Start(collector,AuthenticationDiagnosticPhase.RateConnect)) { }
                using(AuthenticationDiagnostics.Start(collector,AuthenticationDiagnosticPhase.RateLua)) { }
                using(AuthenticationDiagnostics.Start(collector,AuthenticationDiagnosticPhase.RateDecode)) { }
            }
            using var gate=AuthenticationDiagnostics.Start(collector,AuthenticationDiagnosticPhase.GateClaim);
            gate?.Fail(new IOException());
        }
        using var response=Response("{\"code\":\"authentication_audit_unavailable\",\"traceId\":\"request-A\"}");
        using var document=JsonDocument.Parse((await collector.BuildFailureAsync(response,1,0,0,0))!);
        Assert.Equal("incomplete",document.RootElement.GetProperty("diagnosticStatus").GetString());
    }

    [Fact]
    public async Task ACompleteObservedConnectionFailureCanBeDistinguishedFromMissingEvidence()
    {
        var context=new DefaultHttpContext { TraceIdentifier="request-A" };context.Request.Path="/api/v1/auth/login";
        var collector=new LoginDiagnosticCollector(new HttpContextAccessor { HttpContext=context });
        using(AuthenticationDiagnostics.Start(collector,AuthenticationDiagnosticPhase.ServerLogin))
        {
            using var total=AuthenticationDiagnostics.Start(collector,AuthenticationDiagnosticPhase.RateTotal);
            using(var connect=AuthenticationDiagnostics.Start(collector,AuthenticationDiagnosticPhase.RateConnect))
                connect?.Fail(new TimeoutException());
            AuthenticationDiagnostics.NotReached(collector,AuthenticationDiagnosticPhase.RateLua);
            AuthenticationDiagnostics.NotReached(collector,AuthenticationDiagnosticPhase.RateDecode);
            total?.Fail(new TimeoutException());
        }
        using var response=Response("{\"code\":\"login_protection_unavailable\",\"traceId\":\"request-A\"}");
        using var document=JsonDocument.Parse((await collector.BuildFailureAsync(response,1,0,0,0))!);
        Assert.Equal("complete",document.RootElement.GetProperty("diagnosticStatus").GetString());
    }

    [Theory]
    [InlineData(false,"complete")]
    [InlineData(true,"incomplete")]
    public async Task BudgetCancellationCompletenessDistinguishesCallerCancellation(bool callerCancelled,string expectedStatus)
    {
        var context=new DefaultHttpContext { TraceIdentifier="request-budget" };context.Request.Path="/api/v1/auth/login";
        var collector=new LoginDiagnosticCollector(new HttpContextAccessor { HttpContext=context });
        using var budget=new CancellationTokenSource();budget.Cancel();
        using var request=new CancellationTokenSource();if(callerCancelled)request.Cancel();
        using(AuthenticationDiagnostics.Start(collector,AuthenticationDiagnosticPhase.ServerLogin))
        {
            using var total=AuthenticationDiagnostics.Start(collector,AuthenticationDiagnosticPhase.RateTotal,1000);
            using(var connect=AuthenticationDiagnostics.Start(collector,AuthenticationDiagnosticPhase.RateConnect,1000,false))
                connect?.Fail(new OperationCanceledException(budget.Token),request.Token,budget.Token);
            AuthenticationDiagnostics.NotReached(collector,AuthenticationDiagnosticPhase.RateLua);
            AuthenticationDiagnostics.NotReached(collector,AuthenticationDiagnosticPhase.RateDecode);
            total?.Fail(new OperationCanceledException(budget.Token),request.Token,budget.Token);
        }
        using var response=Response("{\"code\":\"login_protection_unavailable\",\"traceId\":\"request-budget\"}");
        using var document=JsonDocument.Parse((await collector.BuildFailureAsync(response,1,0,1000,1000))!);
        Assert.Equal(expectedStatus,document.RootElement.GetProperty("diagnosticStatus").GetString());
        var rate=document.RootElement.GetProperty("events").EnumerateArray().Single(e=>e.GetProperty("phase").GetString()=="RateTotal");
        Assert.Equal(callerCancelled?"RequestCancelled":"BudgetElapsed",rate.GetProperty("error").GetString());
        Assert.Equal(callerCancelled,rate.GetProperty("RequestCancelled").GetBoolean());
        Assert.True(rate.GetProperty("BudgetCancelled").GetBoolean());
        Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);
    }

    [Theory]
    [InlineData("save","incomplete")]
    [InlineData("commit","incomplete")]
    [InlineData("failure","incomplete")]
    [InlineData("none","complete")]
    public async Task AuditFailureCompletenessRequiresSaveCommitAndFailureEvidence(string missing,string expected)
    {
        var context=new DefaultHttpContext { TraceIdentifier="audit-boundary" };context.Request.Path="/api/v1/auth/login";
        var collector=new LoginDiagnosticCollector(new HttpContextAccessor { HttpContext=context });
        using(var server=AuthenticationDiagnostics.Start(collector,AuthenticationDiagnosticPhase.ServerLogin))
        {
            using(AuthenticationDiagnostics.Start(collector,AuthenticationDiagnosticPhase.RateTotal))
            {
                using(AuthenticationDiagnostics.Start(collector,AuthenticationDiagnosticPhase.RateConnect)) { }
                using(AuthenticationDiagnostics.Start(collector,AuthenticationDiagnosticPhase.RateLua)) { }
                using(AuthenticationDiagnostics.Start(collector,AuthenticationDiagnosticPhase.RateDecode)) { }
            }
            using(AuthenticationDiagnostics.Start(collector,AuthenticationDiagnosticPhase.AuditWrite,auditKind:AuthenticationAuditKind.Success))
            {
                using(AuthenticationDiagnostics.Start(collector,AuthenticationDiagnosticPhase.AuditValidate,auditKind:AuthenticationAuditKind.Success)) { }
                using(AuthenticationDiagnostics.Start(collector,AuthenticationDiagnosticPhase.AuditTransaction,auditKind:AuthenticationAuditKind.Success)) { }
                if(missing!="save")using(AuthenticationDiagnostics.Start(collector,AuthenticationDiagnosticPhase.AuditSave,auditKind:AuthenticationAuditKind.Success)) { }
            }
            if(missing!="commit")
                using(var commit=AuthenticationDiagnostics.Start(collector,AuthenticationDiagnosticPhase.AuditCommit,auditKind:AuthenticationAuditKind.Success))
                    if(missing!="failure")commit?.Fail(new TimeoutException());
            server?.Fail(new IOException());
        }
        using var response=Response("{\"code\":\"authentication_audit_unavailable\",\"traceId\":\"audit-boundary\"}");
        using var document=JsonDocument.Parse((await collector.BuildFailureAsync(response,1,0,0,0))!);
        Assert.Equal(expected,document.RootElement.GetProperty("diagnosticStatus").GetString());
    }

    private sealed class ThrowingSink : IAuthenticationDiagnosticSink
    {
        public bool Enabled => true;
        public void Record(in AuthenticationDiagnosticEvent value) => throw new IOException("CANARY-SECRET");
        public void MarkIncomplete() { }
    }
}
