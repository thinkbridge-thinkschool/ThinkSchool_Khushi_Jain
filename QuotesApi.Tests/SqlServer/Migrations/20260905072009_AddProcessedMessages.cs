using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuotesApi.Tests.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddProcessedMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProcessedMessages",
                columns: table => new
                {
                    Subscription = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    MessageId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    QuoteId = table.Column<int>(type: "int", nullable: false),
                    HandledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessedMessages", x => new { x.Subscription, x.MessageId });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProcessedMessages");
        }
    }
}
