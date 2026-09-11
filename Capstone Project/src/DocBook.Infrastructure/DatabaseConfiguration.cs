using Microsoft.Extensions.Configuration;

namespace DocBook.Infrastructure;

public static class DatabaseConfiguration
{
    public const string ConnectionName = "DocBook";

    // Never in a settings file, because the only connection string this app has carries a password.
    public static string ConnectionString(this IConfiguration configuration) =>
        configuration.GetConnectionString(ConnectionName)
        ?? throw new InvalidOperationException(
            "ConnectionStrings:DocBook is required. Export ConnectionStrings__DocBook before running.");
}
