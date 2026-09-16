using Microsoft.EntityFrameworkCore;

namespace DocBook.Infrastructure;

// Shared shapes, each mapped into the writing module's own schema so one transaction covers both.
public static class SharedTables
{
    public const int MaxTypeNameLength = 200;

    public static void MapOutbox(this ModelBuilder builder, string schema)
    {
        builder.Entity<OutboxMessage>(outbox =>
        {
            outbox.ToTable("outbox_messages", schema);
            outbox.HasKey(message => message.Id);
            outbox.Property(message => message.Type).HasMaxLength(MaxTypeNameLength).IsRequired();
            outbox.Property(message => message.Payload).IsRequired();
            outbox.Property(message => message.LastFailure).HasMaxLength(MaxTypeNameLength);

            // The dispatcher only ever asks for unclaimed, unprocessed rows, oldest first.
            outbox
                .HasIndex(message => new { message.ProcessedAt, message.ClaimedUntil, message.OccurredAt })
                .HasDatabaseName("ix_outbox_messages_pending");
        });
    }

    public static void MapAuditTrail(this ModelBuilder builder, string schema)
    {
        builder.Entity<AuditEntry>(audit =>
        {
            audit.ToTable("audit_entries", schema);
            audit.HasKey(entry => entry.Id);
            audit.Property(entry => entry.ActorRole).HasMaxLength(32).IsRequired();
            audit.Property(entry => entry.Action).HasMaxLength(64).IsRequired();
            audit.HasIndex(entry => entry.SubjectId);
        });
    }
}
