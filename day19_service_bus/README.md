# Day 19 — Service Bus topics and the dead-letter queue

One topic on Azure Service Bus, two subscriptions, two competing consumers on one of them, handlers
that dedupe on the message id, and two messages that reach the dead-letter queue by different routes.

## Run it

`azure-setup.sh` creates the namespace, the topic and the two subscriptions, then prints the
connection string on stdout and everything else on stderr. Piping it straight into the run keeps the
shared access key out of the terminal and off disk.

```bash
az login
```

```bash
SERVICE_BUS_CONNECTION_STRING="$(bash day19_service_bus/azure-setup.sh)" dotnet run --project day19_service_bus
```

Delete it when you are finished. The namespace is Standard tier, which bills a base charge by the
hour whether or not anything is sent, so leaving it up costs money for nothing:

```bash
az group delete --name rg-day19-servicebus --yes --no-wait
```

Standard is not optional — the Basic tier supports queues only, so a topic cannot be created on it.

## Topology

| Entity | What it gets |
|---|---|
| `quote-events` | The topic everything is published to |
| `audit` | Every event, `MaxDeliveryCount` 2 |
| `moderation` | Creations only — filter `eventType = 'QuoteCreated'`, `MaxDeliveryCount` 3 |

A new subscription is created with a `$Default` rule that passes everything, so `moderation` needs
that rule deleted as well as its own added. Leaving it in place is a silent bug: the filter looks
configured and the subscription still receives the whole topic. `audit` keeps `$Default`, which is
exactly the "everything" it wants.

The filter is why this is a topic and not a queue. A queue would force one consumer to read
everything and discard what it did not want.

## Publisher

Five messages, and what matters is which share a `MessageId`.

| Message id | What it is |
|---|---|
| `evt-1001` | A creation |
| `evt-1001` | The same event sent twice, as a retrying publisher would |
| `evt-1002` | An archival, so `moderation` never sees it |
| `evt-1003` | `quoteId` is a string, so the body will not deserialize |
| `evt-1004` | Valid, but the audit handler's downstream is down |

`eventType` is sent as an application property because that is what the subscription filter reads.

## Consumer

Two processors on `audit` and one on `moderation`. The two on `audit` are the competing consumers:
separate links to the same subscription, so each message goes to one of them and never both. Two
replicas of a deployed worker are the same thing — competing consumption is a property of the
subscription, not of the process boundary. `PrefetchCount` is 0 so one worker cannot buffer the
whole subscription and leave the other idle.

## Idempotency

Delivery is at-least-once, so the handler is what makes processing exactly-once. The id is claimed
before the work runs:

```csharp
var key = (subscription, message.MessageId);

if (!handled.TryAdd(key, true))
{
    logger.LogInformation("{MessageId}: '{Subscription}' has this id already, skipping the work.", message.MessageId, subscription);
    await args.CompleteMessageAsync(message);

    return;
}
```

The key is `(subscription, messageId)`, not `messageId`. A topic hands the same id to every
subscription, so keying on the id alone would let whichever subscription arrives first silently stop
the others from ever running.

The claim is released when the handler fails. Claiming and then failing would mark the message done
without having done it, and the redelivery would be skipped — a message lost quietly. `TryAdd` is
doing the job an `INSERT` against a unique key does in a real handler, and the release is what the
surrounding transaction does when it rolls back.

Service Bus can also deduplicate on `MessageId` itself, through `RequiresDuplicateDetection`. It is
off here on purpose: it covers only a bounded window and cannot help with a message redelivered
because a handler crashed halfway through.

## Dead-lettering

`evt-1003` cannot be deserialized, and retrying a malformed body never succeeds, so the handler
calls `DeadLetterMessageAsync` on the first delivery.

`evt-1004` fails against a downstream that might come back, so the handler abandons it. Service Bus
redelivers, and once the delivery count passes `MaxDeliveryCount` the broker dead-letters it as
`MaxDeliveryCountExceeded`. No code decides that; the broker stops the loop.

Both are read back at the end of the run through `SubQueue.DeadLetter`. `moderation`'s dead-letter
queue stays empty and both messages are handled there without complaint, because each subscription
holds its own copy with its own delivery count.

The same count is visible in the portal under the namespace's Subscriptions blade, before the
namespace is deleted.

## Proof

```
Namespace sbday19e129e3144dd4 in rg-day19-servicebus (eastus).
Topic quote-events ready: audit takes everything, moderation takes QuoteCreated only.
11:48:36.542 info: day19[0] Published evt-1001 (QuoteCreated).
11:48:36.888 info: day19[0] Published evt-1001 (QuoteCreated).
11:48:37.966 info: day19[0] Published evt-1002 (QuoteArchived).
11:48:39.775 info: day19[0] Published evt-1003 (QuoteCreated).
11:48:40.081 info: day19[0] Published evt-1004 (QuoteCreated).
11:48:40.180 info: day19[0] Two competing consumers on 'audit', one on 'moderation'.
11:48:43.532 info: audit-1[0] Audited QuoteCreated for quote 1001 by Ada Lovelace.
11:48:43.837 info: moderation-1[0] Queued QuoteCreated (evt-1001) for moderation.
11:48:43.837 info: audit-2[0] evt-1001: 'audit' has this id already, skipping the work.
11:48:44.176 info: audit-1[0] Audited QuoteArchived for quote 1002 by Grace Hopper.
11:48:45.122 info: moderation-1[0] evt-1001: 'moderation' has this id already, skipping the work.
11:48:45.125 warn: audit-2[0] evt-1004 delivery 1: The audit ledger is unavailable. Abandoning for redelivery.
11:48:45.133 fail: audit-1[0] evt-1003: The JSON value could not be converted to QuoteEvent. Path: $.quoteId | LineNumber: 0 | BytePositionInLine: 52. Dead-lettering without retrying.
11:48:45.853 info: moderation-1[0] Queued QuoteCreated (evt-1003) for moderation.
11:48:46.186 warn: audit-1[0] evt-1004 delivery 2: The audit ledger is unavailable. Abandoning for redelivery.
11:48:46.455 info: moderation-1[0] Queued QuoteCreated (evt-1004) for moderation.
11:48:56.487 info: day19[0] Handled exactly once: 2 on 'audit', 3 on 'moderation'.
11:49:01.967 info: day19[0] 'audit' dead-letter queue holds 2 message(s).
11:49:01.968 info: day19[0]   evt-1003 after 1 delivery(s) | HandlerRejected: The JSON value could not be converted to QuoteEvent. Path: $.quoteId | LineNumber: 0 | BytePositionInLine: 52. | {"eventType":"QuoteCreated","quoteId":"not-a-number"}
11:49:01.968 info: day19[0]   evt-1004 after 3 delivery(s) | MaxDeliveryCountExceeded: Message could not be consumed after 2 delivery attempts. | {"eventType":"QuoteCreated","quoteId":1004,"author":"Edsger Dijkstra","text":"Simplicity is a prerequisite for reliability."}
11:49:15.298 info: day19[0] 'moderation' dead-letter queue holds 0 message(s).
```

Both workers did work, and no message was handled by both: `audit-1` took `evt-1001`, `evt-1002`
and `evt-1003`, `audit-2` took the duplicate and `evt-1004`.

The two skips are the interesting pair. `audit` refused the duplicate `evt-1001`, and `moderation`
refused its own duplicate separately — but only after processing its own first copy. That is the
`(subscription, messageId)` key working. A key of `messageId` alone would have shown `moderation`
skipping `evt-1001` outright, having never handled it.

`evt-1002` never reaches `moderation`, so the subscription filter is live.

The two dead-letter reasons are the two routes. `evt-1003` left on delivery 1 with
`HandlerRejected`, the reason the handler chose. `evt-1004` left with `MaxDeliveryCountExceeded`,
which no code in this repository sets — the broker wrote it.

`evt-1004` reads "after 3 delivery(s)" against a `MaxDeliveryCount` of 2, which is not an
off-by-one. The handler abandoned deliveries 1 and 2; the broker then counted a third attempt,
saw it would exceed the limit, and dead-lettered instead of delivering. Its own description says
so: consumed after 2 delivery attempts.

`moderation` handled `evt-1003` and `evt-1004` without complaint and its dead-letter queue is
empty, because each subscription holds its own copy with its own delivery count.

## What is not here

No Event Grid. The heading pairs the two, but nothing in the exercise asks for it, and they are not
interchangeable — Service Bus holds messages until a consumer settles them, Event Grid pushes events
and has no dead-letter behaviour of this shape.

No managed identity. The run authenticates with the namespace's shared access key, which is what
`azure-setup.sh` returns. `DefaultAzureCredential` against the namespace's hostname would be the
production choice, and needs a role assignment the exercise does not ask for.

No retry backoff. Abandoning redelivers immediately, which is what keeps the run short.
