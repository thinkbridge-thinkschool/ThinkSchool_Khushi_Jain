using Microsoft.EntityFrameworkCore;
using QuotesApi.Models;

namespace QuotesApi.Data;

public class QuotesDbContext(DbContextOptions<QuotesDbContext> options)
    : DbContext(options)
{
    public DbSet<Quote> Quotes => Set<Quote>();

    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Collection> Collections => Set<Collection>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Text is left out: SQL Server cannot key an index on nvarchar(max).
        modelBuilder.Entity<Quote>()
            .HasIndex(q => new { q.Author, q.IsDeleted });

        modelBuilder.Entity<Collection>(collection =>
        {
            collection.Property(c => c.Name)
                .HasMaxLength(Collection.MaximumNameLength);

            // CollectionItem is a value object with no identity of its own, so
            // it is mapped as an owned type: its rows live and die with the
            // collection and cannot be queried or saved independently. The
            // shadow key exists only because a relational table needs one.
            collection.OwnsMany(c => c.Items, item =>
            {
                item.ToTable("CollectionItems");
                item.WithOwner().HasForeignKey("CollectionId");
                item.Property<int>("Id");
                item.HasKey("Id");
            });
        });
    }
}
