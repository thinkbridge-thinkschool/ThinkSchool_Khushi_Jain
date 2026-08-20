# Day 10 — Change Tracking

[`ChangeTrackingTests.cs`](ChangeTrackingTests.cs) seeds 10,000 `Quote` rows into an
in-memory SQLite database and reads them back both ways. Same database, same query,
same columns, same rows — tracking is the only difference.

**Tracking query**

```csharp
await db.Quotes.ToListAsync();
```

**AsNoTracking query**

```csharp
await db.Quotes.AsNoTracking().ToListAsync();
```

## Observed result

10,000 rows, mean of 5 reads per variant with a fresh `DbContext` each read, after a
discarded warm-up round:

| | Time | Allocated |
|---|---|---|
| Tracking | 125.5 ms | 9,883 KB |
| `AsNoTracking` | 29.1 ms | 4,056 KB |
| Difference | 4.3× faster | 5,827 KB less (59%) |

Across three runs the tracked read took 97.8–125.5 ms and the untracked one 29.1–31.7 ms.
The allocation figures barely move: 9,847–9,883 KB against 4,056 KB every time.

The absolute times are flattering because this reads from in-memory SQLite. Tracking
overhead is CPU and memory only, so the shape holds anywhere, but against a remote
database the I/O would dominate and the time gap would be a smaller share of the total.
The allocation figure is the part that transfers.

## Tracking behaviour

| | Tracking | `AsNoTracking` |
|---|---|---|
| `ChangeTracker.Entries<Quote>()` after the read | 10,000 | 0 |
| State of a returned entity | `Unchanged` | not tracked |
| Same query run twice in one context | same 10,000 instances returned again | 10,000 new instances |

The second point is identity resolution: the repeat query still reads all 10,000 rows
from the database, but the tracker already holds those keys, so EF Core hands back the
instances it already has instead of building new ones. The tracker count stays at
10,000. Without tracking there is nothing to match against, so every read materialises
fresh objects.

That extra bookkeeping — a snapshot of every property per entity, plus the identity map
— is what the numbers above are paying for.

## When not to use AsNoTracking

Don't use it when the entities you read will be modified and saved through the same
`DbContext`, because `SaveChanges` only writes what the change tracker is watching.

## Run it

```bash
dotnet test QuotesApi.Tests/QuotesApi.Tests.csproj --filter "FullyQualifiedName~ChangeTrackingTests" -c Release --logger "console;verbosity=detailed"
```
