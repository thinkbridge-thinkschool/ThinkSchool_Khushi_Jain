using Microsoft.EntityFrameworkCore;
using QuotesApi.Models;

namespace QuotesApi.Data;

public class QuotesDbContext(DbContextOptions<QuotesDbContext> options)
    : DbContext(options)
{
    public DbSet<Quote> Quotes => Set<Quote>();

    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
}