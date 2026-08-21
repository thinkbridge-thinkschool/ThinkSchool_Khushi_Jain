using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuotesApi.Tests.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddQuoteAuthorIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Author",
                table: "Quotes",
                type: "nvarchar(450)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_Author_IsDeleted",
                table: "Quotes",
                columns: new[] { "Author", "IsDeleted" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Quotes_Author_IsDeleted",
                table: "Quotes");

            migrationBuilder.AlterColumn<string>(
                name: "Author",
                table: "Quotes",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)");
        }
    }
}
