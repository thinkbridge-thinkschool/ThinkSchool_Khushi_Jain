using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocBook.Scheduling.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DeadLetterOutboxMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_outbox_messages_pending",
                schema: "scheduling",
                table: "outbox_messages");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AbandonedAt",
                schema: "scheduling",
                table: "outbox_messages",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_pending",
                schema: "scheduling",
                table: "outbox_messages",
                columns: new[] { "ProcessedAt", "AbandonedAt", "ClaimedUntil", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_outbox_messages_pending",
                schema: "scheduling",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "AbandonedAt",
                schema: "scheduling",
                table: "outbox_messages");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_pending",
                schema: "scheduling",
                table: "outbox_messages",
                columns: new[] { "ProcessedAt", "ClaimedUntil", "OccurredAt" });
        }
    }
}
