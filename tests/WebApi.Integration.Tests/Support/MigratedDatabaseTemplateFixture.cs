using Xunit;
namespace WebApi.Integration.Tests.Support;

// Lazy creation avoids doing extra migrations when only default-path cases are selected.
public sealed class MigratedDatabaseTemplateFixture : IAsyncLifetime
{
    public MigratedDatabaseTemplate Template { get; } = new();
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Template.DisposeAsync().AsTask();
}
