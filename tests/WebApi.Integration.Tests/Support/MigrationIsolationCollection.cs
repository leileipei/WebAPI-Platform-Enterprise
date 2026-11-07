using Xunit;

namespace WebApi.Integration.Tests.Support;

// These tests share PostgreSQL roles and Npgsql's process-wide connection pool.
// Run them without parallel test collections, even though their databases are unique.
[CollectionDefinition("MigrationIsolation", DisableParallelization = true)]
public sealed class MigrationIsolationCollection { }
