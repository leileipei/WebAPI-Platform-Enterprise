namespace WebApi.Integration.Tests.Support;

/// <summary>Preserves owned test fixtures when an isolated acceptance run opts in.</summary>
public static class IsolatedFixtureRetention
{
    public static async Task CleanupAsync(Func<Task> cleanup, bool? retain = null)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        if (retain ?? Environment.GetEnvironmentVariable("WEBAPI_TEST_RETAIN_DATABASES") == "1")
        {
            Console.WriteLine("isolated-fixture-cleanup-skipped-retained");
            return;
        }
        await cleanup();
    }
}
