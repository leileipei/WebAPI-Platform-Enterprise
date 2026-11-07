using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WebApi.ControlPlane.Security;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class GovernanceClockProblemMappingTests
{
    [Theory]
    [InlineData("security_authoritative_fact_in_future", PostgresErrorCodes.CheckViolation, 503, "security_clock_unavailable")]
    [InlineData("other_check_violation", PostgresErrorCodes.CheckViolation, 422, "invalid_reference")]
    [InlineData("security_authoritative_fact_in_future", PostgresErrorCodes.ForeignKeyViolation, 422, "invalid_reference")]
    [InlineData("security_authoritative_fact_in_future", PostgresErrorCodes.UniqueViolation, 409, "duplicate_resource")]
    public async Task OnlyTheExactFutureFactGuardIsReportedAsUnavailable(string message, string sqlState, int status, string code)
    {
        await using var services = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();
        var postgres = new PostgresException(message, "ERROR", "ERROR", sqlState);
        await ProblemDetailsMapping.HandleAsync(context, () => Task.FromException(new DbUpdateException("database write failed", postgres)));
        Assert.Equal(status, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
    }
}
