using Microsoft.EntityFrameworkCore;

namespace DocBook.Notifications.Infrastructure;

public sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : DbContext(options)
{
    public const string Schema = "notifications";

    public DbSet<HandledMessage> HandledMessages => Set<HandledMessage>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema(Schema);

        builder.Entity<HandledMessage>(message =>
        {
            message.ToTable("handled_messages", Schema);

            // The composite key is the whole rule: one message of one kind per appointment, ever.
            message.HasKey(record => new { record.AppointmentId, record.Kind });

            // Stored as its name, so a reordered enum cannot silently repoint existing rows.
            message.Property(record => record.Kind).HasConversion<string>().HasMaxLength(32);
        });
    }
}
