using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

const string QueueName = "quote-outbox";

var phase = args.FirstOrDefault();

using var loggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss.fff ";
}));

var log = loggerFactory.CreateLogger("day20");

if (phase is not ("crash" or "resume"))
{
    log.LogError("Usage: dotnet run --project day20_outbox -- <crash|resume>");

    return 1;
}

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var path = Path.Combine(AppContext.BaseDirectory, "day20-outbox.db");

await using var db = new OutboxDbContext($"Data Source={path}");
await using var client = new ServiceBusClient(
    Environment.GetEnvironmentVariable("SERVICE_BUS_CONNECTION_STRING")
    ?? throw new InvalidOperationException("Set SERVICE_BUS_CONNECTION_STRING. See day20_outbox/README.md."));

await using var sender = client.CreateSender(QueueName);

if (phase == "crash")
{
    await db.Database.EnsureDeletedAsync();
    await db.Database.EnsureCreatedAsync();
    await PurgeAsync();

    await CreateQuoteAsync("Rejected Author", "Something moderation will not allow.", rejected: true);
    log.LogInformation(
        "After the rollback: {Quotes} quote(s), {Outbox} outbox row(s).",
        await db.Quotes.CountAsync(),
        await db.Outbox.CountAsync());

    await CreateQuoteAsync("Ada Lovelace", "The Analytical Engine weaves algebraic patterns.", rejected: false);
    await RelayAsync(crashAfterPublish: true);

    return 0;
}

if (!File.Exists(path))
{
    log.LogError("No database at {Path}. Run the crash phase first.", path);

    return 1;
}

log.LogInformation("Reopened {Path} in a new process.", path);
await ReportAsync();
await RelayAsync(crashAfterPublish: false);
await ConsumeAsync();
await ReportAsync();

return 0;

// An explicit transaction rather than one save, because the payload needs the identity value.
async Task CreateQuoteAsync(string author, string text, bool rejected)
{
    await using var transaction = await db.Database.BeginTransactionAsync();

    var quote = new Quote { Author = author, Text = text, CreatedAt = DateTimeOffset.UtcNow };

    db.Quotes.Add(quote);
    await db.SaveChangesAsync();

    quote.OutboxMessages.Add(new OutboxMessage
    {
        MessageId = $"quote-created-{quote.Id}",
        EventType = "QuoteCreated",
        Payload = JsonSerializer.Serialize(new QuoteCreated(quote.Id, quote.Author, quote.Text), json),
        OccurredAt = DateTimeOffset.UtcNow,
    });

    await db.SaveChangesAsync();

    // Returning without a commit: disposing the transaction takes the quote and its event together.
    if (rejected)
    {
        db.ChangeTracker.Clear();
        log.LogInformation("Moderation rejected quote {QuoteId} after its outbox row was written.", quote.Id);

        return;
    }

    await transaction.CommitAsync();
    log.LogInformation("Committed quote {QuoteId} and its outbox row in one transaction.", quote.Id);
}

async Task RelayAsync(bool crashAfterPublish)
{
    var pending = await db.Outbox
        .Include(message => message.Quote)
        .Where(message => message.ProcessedAt == null)
        .OrderBy(message => message.Id)
        .Take(50)
        .ToListAsync();

    log.LogInformation("Relay found {Count} unsent row(s).", pending.Count);

    foreach (var message in pending)
    {
        // Saved before the send, so an attempt that dies mid-flight still leaves evidence.
        message.AttemptCount++;
        await db.SaveChangesAsync();

        await sender.SendMessageAsync(new ServiceBusMessage(BinaryData.FromString(message.Payload))
        {
            MessageId = message.MessageId,
            Subject = message.EventType,
            ContentType = "application/json",
        });

        log.LogInformation(
            "Published {MessageId} for {Author} on attempt {Attempt}.",
            message.MessageId,
            message.Quote.Author,
            message.AttemptCount);

        // Exit rather than throw: nothing unwinds and nothing disposes, which is a crash's shape.
        if (crashAfterPublish)
        {
            log.LogWarning("Killing the process before ProcessedAt is written. Row {Id} stays unsent.", message.Id);
            Environment.Exit(70);
        }

        message.ProcessedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        log.LogInformation("Marked {MessageId} sent.", message.MessageId);
    }
}

async Task ConsumeAsync()
{
    await using var receiver = client.CreateReceiver(QueueName);

    var deliveries = await receiver.ReceiveMessagesAsync(maxMessages: 32, maxWaitTime: TimeSpan.FromSeconds(5));
    var applied = 0;

    foreach (var delivery in deliveries)
    {
        if (await db.ProcessedMessages.AnyAsync(processed => processed.MessageId == delivery.MessageId))
        {
            log.LogInformation("{MessageId} was handled before, so the work is skipped.", delivery.MessageId);
            await receiver.CompleteMessageAsync(delivery);

            continue;
        }

        var published = delivery.Body.ToObjectFromJson<QuoteCreated>(json)!;
        var quote = await db.Quotes.FindAsync(published.QuoteId);

        await using var transaction = await db.Database.BeginTransactionAsync();

        // The work and the record of having done it are one commit.
        db.ProcessedMessages.Add(new ProcessedMessage
        {
            MessageId = delivery.MessageId,
            HandledAt = DateTimeOffset.UtcNow,
        });

        quote!.NotifiedCount++;

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        await receiver.CompleteMessageAsync(delivery);

        applied++;
        log.LogInformation("Handled {MessageId}: notified the followers of {Author}.", delivery.MessageId, published.Author);
    }

    log.LogInformation("{Deliveries} delivery(s) received, {Applied} applied.", deliveries.Count, applied);
}

async Task ReportAsync()
{
    foreach (var message in await db.Outbox.AsNoTracking().Include(message => message.Quote).OrderBy(message => message.Id).ToListAsync())
    {
        log.LogInformation(
            "outbox {Id} {MessageId}: {Attempts} attempt(s), sent {Sent}, {Author} notified {Notified} time(s).",
            message.Id,
            message.MessageId,
            message.AttemptCount,
            message.ProcessedAt?.ToLocalTime().ToString("HH:mm:ss") ?? "not yet",
            message.Quote.Author,
            message.Quote.NotifiedCount);
    }

    log.LogInformation("{Processed} message id(s) on record.", await db.ProcessedMessages.CountAsync());
}

async Task PurgeAsync()
{
    await using var receiver = client.CreateReceiver(QueueName);

    var left = await receiver.ReceiveMessagesAsync(maxMessages: 32, maxWaitTime: TimeSpan.FromSeconds(2));

    foreach (var message in left)
    {
        await receiver.CompleteMessageAsync(message);
    }

    if (left.Count > 0)
    {
        log.LogInformation("Cleared {Count} message(s) left on the queue by an earlier run.", left.Count);
    }
}

public sealed record QuoteCreated(int QuoteId, string Author, string Text);

public sealed class Quote
{
    public int Id { get; set; }
    public string Author { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public int NotifiedCount { get; set; }
    public List<OutboxMessage> OutboxMessages { get; } = [];
}

public sealed class OutboxMessage
{
    public long Id { get; set; }
    public int QuoteId { get; set; }
    public Quote Quote { get; set; } = null!;
    public string MessageId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public int AttemptCount { get; set; }
}

public sealed class ProcessedMessage
{
    public string MessageId { get; set; } = string.Empty;
    public DateTimeOffset HandledAt { get; set; }
}

public sealed class OutboxDbContext(string connectionString) : DbContext
{
    public DbSet<Quote> Quotes => Set<Quote>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    protected override void OnConfiguring(DbContextOptionsBuilder options) =>
        options.UseSqlite(connectionString);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Restrict, not Cascade: deleting a quote whose event is unsent would delete the event.
        modelBuilder.Entity<Quote>()
            .HasMany(quote => quote.OutboxMessages)
            .WithOne(message => message.Quote)
            .HasForeignKey(message => message.QuoteId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<OutboxMessage>(outbox =>
        {
            outbox.ToTable("Outbox");

            // The id the consumer dedupes on, so it is unique on this side too.
            outbox.HasIndex(message => message.MessageId).IsUnique();

            // The relay only ever asks for unsent rows, so the index covers only those.
            outbox.HasIndex(message => message.Id)
                .HasFilter("ProcessedAt IS NULL")
                .HasDatabaseName("IX_Outbox_Unsent");
        });

        // The message id is the key, and that key is what stops the same message being handled twice.
        modelBuilder.Entity<ProcessedMessage>()
            .HasKey(message => message.MessageId);
    }
}
