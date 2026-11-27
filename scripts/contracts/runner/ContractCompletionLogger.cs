using System.Text.Json;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;

namespace WebApi.Contracts.TestLogger;

[ExtensionUri("logger://webapi/contract-completion/v1")]
[FriendlyName("WebApiContractCompletion")]
public sealed class ContractCompletionLogger : ITestLogger
{
    public void Initialize(TestLoggerEvents events, string testRunDirectory)
    {
        var owner = Environment.GetEnvironmentVariable("WEBAPI_CONTRACT_TEST_OWNER");
        var path = Environment.GetEnvironmentVariable("WEBAPI_CONTRACT_RESULT")
            ?? throw new InvalidOperationException("Missing contract result path.");
        events.TestRunComplete += (_, run) =>
        {
            var statistics = run.TestRunStatistics;
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(file, new
            {
                owner,
                completed = !run.IsAborted && !run.IsCanceled && run.Error is null,
                executed = statistics?.ExecutedTests,
                passed = statistics?[TestOutcome.Passed],
                skipped = statistics?[TestOutcome.Skipped],
                failed = statistics?[TestOutcome.Failed]
            });
        };
    }
}
