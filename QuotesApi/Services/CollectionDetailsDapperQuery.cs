using System.Globalization;
using Dapper;
using Microsoft.EntityFrameworkCore;
using QuotesApi.Data;

namespace QuotesApi.Services;

// Hand-written equivalent of CollectionDetailsQuery, for the EF-vs-Dapper comparison.
public sealed class CollectionDetailsDapperQuery(QuotesDbContext db)
{
    public const string Sql = """
        SELECT  c.Id, c.Name, c.OwnerId,
                i.Id AS ItemId, q.Id AS QuoteId, q.Author, q.Text, i.AddedAt
        FROM Collections AS c
        LEFT JOIN CollectionItems AS i ON i.CollectionId = c.Id
        LEFT JOIN Quotes AS q ON q.Id = i.QuoteId AND q.IsDeleted = 0
        WHERE c.Id = @id
        ORDER BY i.Id
        """;

    public async Task<CollectionDetails?> RunAsync(
        int id,
        CancellationToken cancellationToken)
    {
        var rows = (await db.Database.GetDbConnection().QueryAsync<Row>(
            new CommandDefinition(Sql, new { id }, cancellationToken: cancellationToken)))
            .AsList();

        if (rows.Count == 0)
        {
            return null;
        }

        return new CollectionDetails(
            rows[0].Id,
            rows[0].Name,
            rows[0].OwnerId,
            rows.Count(row => row.ItemId is not null),
            rows
                .Where(row => row.QuoteId is not null)
                .Select(row => new CollectionDetailsItem(
                    row.QuoteId!.Value,
                    row.Author!,
                    row.Text!,
                    DateTimeOffset.Parse(row.AddedAt!, CultureInfo.InvariantCulture)))
                .ToList());
    }

    // SQLite has no datetimeoffset, so AddedAt arrives as the TEXT that EF wrote.
    private sealed class Row
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string OwnerId { get; set; } = string.Empty;
        public int? ItemId { get; set; }
        public int? QuoteId { get; set; }
        public string? Author { get; set; }
        public string? Text { get; set; }
        public string? AddedAt { get; set; }
    }
}
