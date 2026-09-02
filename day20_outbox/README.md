# Day 20 — The transactional outbox

A quote and the event announcing it are saved in one transaction. A relay publishes the event to a
Service Bus queue afterwards and marks the row sent. I kill the process between those two steps to
show the message is not lost.

## Run it

```bash
az login
```

```bash
export SERVICE_BUS_CONNECTION_STRING="$(bash day20_outbox/azure-setup.sh)"
```

```bash
dotnet run --project day20_outbox -- crash
```

```bash
dotnet run --project day20_outbox -- resume
```

`crash` ends with exit code 70 on purpose. The SQLite file sits next to the binary in
`bin/Debug/net10.0/` and is what carries state from the first run into the second.

```bash
az group delete --name rg-day20-outbox --yes --no-wait
```

## The outbox table

```csharp
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
```

| Column | Why |
|---|---|
| `QuoteId` | The quote this event belongs to. `Restrict` on delete, so a quote cannot be deleted while its event is unsent |
| `MessageId` | `quote-created-42`, unique. The consumer dedupes on it, so it must be stable across republishes |
| `ProcessedAt` | Null means unsent. That is the relay's whole queue |
| `AttemptCount` | Saved before each publish, so an attempt that dies mid-flight still leaves evidence |

The index the relay uses is filtered, because nearly every row is already sent:

```csharp
outbox.HasIndex(message => message.Id)
    .HasFilter("ProcessedAt IS NULL")
    .HasDatabaseName("IX_Outbox_Unsent");
```

## The one transaction

Two saves, one transaction — the payload needs the quote's id, which only exists after the first save.

```csharp
await using var transaction = await db.Database.BeginTransactionAsync();

db.Quotes.Add(quote);
await db.SaveChangesAsync();

quote.OutboxMessages.Add(new OutboxMessage { ... });
await db.SaveChangesAsync();

await transaction.CommitAsync();
```

`crash` also runs a quote that moderation rejects after the outbox row is written. Nothing commits,
and both rows disappear together, so there is never an event for a change that did not happen.

## The relay

```csharp
var pending = await db.Outbox
    .Include(message => message.Quote)
    .Where(message => message.ProcessedAt == null)
    .OrderBy(message => message.Id)
    .Take(50)
    .ToListAsync();

foreach (var message in pending)
{
    message.AttemptCount++;
    await db.SaveChangesAsync();

    await sender.SendMessageAsync(new ServiceBusMessage(BinaryData.FromString(message.Payload))
    {
        MessageId = message.MessageId,
        Subject = message.EventType,
        ContentType = "application/json",
    });

    message.ProcessedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();
}
```

Publish first, mark second. The other order loses the message: mark sent, crash, and nothing ever
publishes it.

## The crash I tested

`Environment.Exit(70)` between the publish and the `ProcessedAt` save. Exit does not unwind the
stack, so no `finally` runs and nothing disposes.

The message is on the queue and the row still says unsent. `resume` is a second process against the
same file: the relay finds the same row pending, publishes it again, and marks it sent. Its attempt
count reads 2.

## Why nothing is lost or duplicated

Every crash point leaves the truth in one place. Before the commit, neither row exists. After it, the
outbox row is there and unsent, so any later relay run picks it up. After the publish, it is still
unsent, so it publishes again.

That last case means delivery is at-least-once, not exactly-once. The queue really does get the same
message twice here.

The duplicate stops at the consumer. The message id is the primary key of `ProcessedMessages`, and
the row is written in the same transaction as the work:

```csharp
db.ProcessedMessages.Add(new ProcessedMessage { MessageId = delivery.MessageId, ... });
quote.NotifiedCount++;

await db.SaveChangesAsync();
await transaction.CommitAsync();
```

Two deliveries, one notification — `NotifiedCount` stays 1. The lookup before the insert is a fast
path; the primary key is the guarantee. If two consumers raced, the loser's insert would fail and the
redelivery would take the skip path instead.

## Proof

```
11:18:56.407 info: day20[0] Moderation rejected quote 1 after its outbox row was written.
11:18:56.665 info: day20[0] After the rollback: 0 quote(s), 0 outbox row(s).
11:18:56.672 info: day20[0] Committed quote 1 and its outbox row in one transaction.
11:18:56.822 info: day20[0] Relay found 1 unsent row(s).
11:18:57.855 info: day20[0] Published quote-created-1 for Ada Lovelace on attempt 1.
11:18:57.855 warn: day20[0] Killing the process before ProcessedAt is written. Row 1 stays unsent.
```

```
11:19:09.980 info: day20[0] Reopened day20-outbox.db in a new process.
11:19:11.182 info: day20[0] outbox 1 quote-created-1: 1 attempt(s), sent not yet, Ada Lovelace notified 0 time(s).
11:19:11.211 info: day20[0] 0 message id(s) on record.
11:19:11.369 info: day20[0] Relay found 1 unsent row(s).
11:19:15.216 info: day20[0] Published quote-created-1 for Ada Lovelace on attempt 2.
11:19:15.228 info: day20[0] Marked quote-created-1 sent.
11:19:17.051 info: day20[0] Handled quote-created-1: notified the followers of Ada Lovelace.
11:19:17.057 info: day20[0] quote-created-1 was handled before, so the work is skipped.
11:19:17.483 info: day20[0] 2 delivery(s) received, 1 applied.
11:19:17.728 info: day20[0] outbox 1 quote-created-1: 2 attempt(s), sent 11:19:15, Ada Lovelace notified 1 time(s).
11:19:17.728 info: day20[0] 1 message id(s) on record.
```

The rejected quote left nothing behind — 0 quotes and 0 outbox rows — so the transaction covers the
event as well as the domain change.

The second process opened on `1 attempt(s), sent not yet`. The publish had happened and the mark had
not, which is the state the crash was aimed at, and the row was still there to be found.

`2 delivery(s) received, 1 applied` is the at-least-once trade in one line: the queue held the
message twice, and `notified 1 time(s)` says the work ran once.

## What would break this

Two relays would both read the same unsent row and publish it. The consumer absorbs it, but a real
deployment claims rows first, with a lease column or an `UPDATE … RETURNING`.

Nothing prunes sent rows, so the table needs a retention job.

A consumer whose side effect is not a database write — sending a real email — cannot put the effect
and the record of it in one transaction, so the same problem comes back one layer down.
