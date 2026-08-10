using Microsoft.EntityFrameworkCore;
using QuotesApi.Models;

namespace QuotesApi.Data;

public class QuotesDbContext(DbContextOptions options)
    : DbContext(options)
{
    public DbSet<Quote> Quotes => Set<Quote>();

    public DbSet<Collection> Collections => Set<Collection>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Collection>(entity =>
        {
            entity.HasKey(c => c.Id);

            entity.Property(c => c.Name)
                .IsRequired()
                .HasMaxLength(80);

            entity.OwnsMany(c => c.Items, item =>
            {
                item.WithOwner()
                    .HasForeignKey("CollectionId");

                item.Property(i => i.QuoteId)
                    .IsRequired();

                item.Property(i => i.AddedAt)
                    .IsRequired();

                item.HasKey("CollectionId", "QuoteId");

                item.Property<int>("CollectionId")
                    .ValueGeneratedNever();

                item.Property(i => i.QuoteId)
                    .ValueGeneratedNever();
            });
        });
    }
}