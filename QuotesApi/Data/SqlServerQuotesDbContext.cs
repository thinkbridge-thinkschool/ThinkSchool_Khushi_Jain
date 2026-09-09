using Microsoft.EntityFrameworkCore;

namespace QuotesApi.Data;

/// <summary>
/// The same model against SQL Server. It exists because EF Core finds a
/// migration by the context it is attributed to, not by the provider that
/// generated it: two sets in one assembly under one context would both be
/// applied, so the SQL Server set is attributed here and the SQLite set stays
/// on <see cref="QuotesDbContext"/>. Everything in the app still injects the
/// base type.
/// </summary>
public sealed class SqlServerQuotesDbContext(DbContextOptions<SqlServerQuotesDbContext> options)
    : QuotesDbContext(options)
{
}
