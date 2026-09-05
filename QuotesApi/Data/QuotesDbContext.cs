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
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

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

        modelBuilder.Entity<OutboxMessage>(outbox =>
        {
            outbox.ToTable("Outbox");

            // The id a consumer dedupes on, so a duplicate cannot be written on this side either.
            outbox.HasIndex(message => message.MessageId).IsUnique();

            // The relay only ever asks for unsent rows, so the index covers only those.
            outbox.HasIndex(message => message.Id)
                .HasFilter("ProcessedAt IS NULL")
                .HasDatabaseName("IX_Outbox_Unsent");
        });

        modelBuilder.Entity<ProcessedMessage>(processed =>
        {
            // The composite key is the idempotency guarantee itself: a second
            // delivery collides with it rather than doing the work again.
            processed.HasKey(message => new { message.Subscription, message.MessageId });

            // Both are lengthed because a key column cannot be nvarchar(max).
            processed.Property(message => message.Subscription)
                .HasMaxLength(ProcessedMessage.MaximumSubscriptionLength);

            processed.Property(message => message.MessageId)
                .HasMaxLength(ProcessedMessage.MaximumMessageIdLength);
        });
    }
}
