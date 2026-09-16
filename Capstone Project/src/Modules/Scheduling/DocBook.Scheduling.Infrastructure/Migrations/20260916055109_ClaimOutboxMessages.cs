using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocBook.Scheduling.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ClaimOutboxMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_outbox_messages_pending",
                schema: "scheduling",
                table: "outbox_messages");

            migrationBuilder.AddColumn<Guid>(
                name: "ClaimedBy",
                schema: "scheduling",
                table: "outbox_messages",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ClaimedUntil",
                schema: "scheduling",
                table: "outbox_messages",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_pending",
                schema: "scheduling",
                table: "outbox_messages",
                columns: new[] { "ProcessedAt", "ClaimedUntil", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_outbox_messages_pending",
                schema: "scheduling",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "ClaimedBy",
                schema: "scheduling",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "ClaimedUntil",
                schema: "scheduling",
                table: "outbox_messages");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_pending",
                schema: "scheduling",
                table: "outbox_messages",
                columns: new[] { "ProcessedAt", "OccurredAt" });
        }
    }
}
