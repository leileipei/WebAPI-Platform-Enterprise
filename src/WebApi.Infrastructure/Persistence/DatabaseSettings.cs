using Npgsql;
namespace WebApi.Infrastructure.Persistence;
public static class DatabaseSettings
{
    public static string ConnectionString()
    {
        var passwordFile = System.Environment.GetEnvironmentVariable("WEBAPI_DB_PASSWORD_FILE") ?? "/run/secrets/postgres-password";
        if (!File.Exists(passwordFile)) throw new InvalidOperationException("Database password file is required.");
        return new NpgsqlConnectionStringBuilder {
            Host = System.Environment.GetEnvironmentVariable("WEBAPI_DB_HOST") ?? "postgres",
            Database = System.Environment.GetEnvironmentVariable("WEBAPI_DB_NAME") ?? "webapi",
            Username = System.Environment.GetEnvironmentVariable("WEBAPI_DB_USER") ?? "webapi",
            Password = File.ReadAllText(passwordFile).Trim(),
            IncludeErrorDetail = false
        }.ConnectionString;
    }
}
