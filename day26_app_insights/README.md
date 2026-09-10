# Day 26 — App Insights + KQL

The API already exported OpenTelemetry to Application Insights, so this day was about making the
data usable: the queries in [`queries.kql`](queries.kql), an alert on error rate, and getting one
trace to run from the HTTP request all the way to the consumer's database write.

## What sends the data

| Signal | Source |
|---|---|
| Requests, dependencies, spans | OpenTelemetry in `QuotesApi/Extensions/InfrastructureExtensions.cs` |
| Log lines | Serilog, forwarded to the OpenTelemetry provider with `writeToProviders: true` |
| Connection string | Key Vault, resolved into `APPLICATIONINSIGHTS_CONNECTION_STRING` by the container app |

## Making the trace stitch

The worker is a `BackgroundService` inside the API process, so one trace covers both. It still took
three fixes, because the chain was broken in three places.

**The broker's spans were not being collected.** The Azure SDK publishes them on an `ActivitySource`
named `Azure.Messaging.ServiceBus`, and OpenTelemetry only listens to sources you name. Nothing named
it, so there were no send or receive spans at all — and because `StartActivity` returns null when no
one is listening, the consumer ran with no current activity and its SQL writes became roots of their
own traces. Adding `.AddSource("Azure.*")` fixed both symptoms at once.

**The outbox loses the trace by design.** The request writes a row and returns; the relay publishes
it up to a minute later on its own thread, with nothing connecting the two. So the row now carries
the request's `traceparent`, and the relay starts its `outbox-publish` span with that as the parent.
Everything the publish causes lands back in the trace of the request that caused it.

**The SDK keeps its spans behind a switch.** Nothing came from the broker until I set
`Azure.Experimental.EnableActivitySource` in `Program.cs`. Without it the send was invisible and the
consumer ran with no ambient span at all, so its database work opened a trace of its own; with it on,
the SDK's receive span joins the publisher's trace. Both subscriptions then produce identically named
`ServiceBusProcessor.ProcessMessage` spans, so the handler also starts a `consume-{subscription}` span
from the message's `traceparent` — that is what tells `audit` and `moderation` apart in the trace.

The result is one trace with these legs:

| Leg | Comes from |
|---|---|
| `POST /api/quotes/` | ASP.NET Core instrumentation |
| The quote and outbox inserts | SQL Client instrumentation |
| `outbox-publish` | My own span in `OutboxRelay`, parented to the stored `traceparent` |
| `Message`, `ServiceBusSender.Send`, `ServiceBusProcessor.ProcessMessage` | Azure Service Bus SDK |
| `consume-audit` and `consume-moderation` | My own spans in `QuoteEventsConsumer`, naming the subscription |
| The dedupe read and `ProcessedMessages` insert | SQL Client instrumentation |

Screenshot of the trace: [`trace.png`](trace.png).

## The queries

| # | Answers |
|---|---|
| 1 | p50 and p99 per endpoint, slowest tail first |
| 2 | Which dependency the app actually waits on, by total time |
| 3 | Error rate per five minutes |
| 4 | The same measure the alert rule evaluates |
| 5 | Every span in one create, in order, with its parent |

Two things to know before reading those numbers. Query 2 is dominated by `ServiceBusReceiver.Receive`
— the consumers' idle long-polls, a few per minute whether or not anything is published. And the
counts weight by `itemCount` rather than using `count()`, because the exporter samples: telemetry here
came back with `microsoft.sample_rate` of 16.67, so one retained item stands for about six.

## The alert

`infra/modules/alerts.bicep` — a log alert on query 4, evaluated every five minutes over a
fifteen-minute window, at 5% failed requests in dev and 2% in prod, emailing an action group. It is
only created when an address is set, since a rule nobody receives raises nothing:

```bash
azd env set AZURE_ALERT_EMAIL you@example.com && azd up
```

## Run it

```bash
curl -s -X POST "$API/api/quotes" -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d '{"author":"Ada","text":"Trace me"}'
```

The relay wakes on the write, so the publish follows within a second. In the portal the trace is
under Investigate → Transaction search; the queries run under Monitoring → Logs.
