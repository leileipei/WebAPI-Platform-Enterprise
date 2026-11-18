using Xunit;
namespace WebApi.Integration.Tests.Support;

// Lazy creation avoids doing extra migrations when only default-path cases are selected.
public class MigratedDatabaseTemplateFixture : IAsyncLifetime
{
    public MigratedDatabaseTemplateFixture() : this(new MigratedDatabaseTemplate()) { }
    protected MigratedDatabaseTemplateFixture(MigratedDatabaseTemplate template) => Template = template;
    public MigratedDatabaseTemplate Template { get; }
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Template.DisposeAsync().AsTask();
}

public sealed class ImportSessionDatabaseTemplateFixture()
    : MigratedDatabaseTemplateFixture(new MigratedDatabaseTemplate(ContractDatabaseGroup.ImportSession))
{
}
