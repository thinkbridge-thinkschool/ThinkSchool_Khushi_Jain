# Day 8 — clustered vs non-clustered indexes

Create a clustered index and two non-clustered indexes on a table with ~100k
rows, generating the data. Use `SET STATISTICS IO ON` and the actual execution
plan.

Script: [`index_lab.sql`](index_lab.sql). Captured run:
[`results/index_lab.txt`](results/index_lab.txt). See
[sql/README.md](../README.md) for how to run it.

## Data

`perf.QuoteView`, 100,000 view-log rows, built as a heap so every "before"
number is the table with no index at all. Columns derive from `HASHBYTES` of the
row number, so the run is reproducible. `ViewedAt` rises with `ViewId`;
`CountryCode` is skewed, with `MT` at 26 rows out of 100,000.

## Index DDL

```sql
CREATE UNIQUE CLUSTERED INDEX CIX_QuoteView_ViewedAt
    ON perf.QuoteView (ViewedAt, ViewId);

CREATE NONCLUSTERED INDEX IX_QuoteView_QuoteId_ViewedAt
    ON perf.QuoteView (QuoteId, ViewedAt DESC, ViewId DESC)
    INCLUDE (DwellSeconds);

CREATE NONCLUSTERED INDEX IX_QuoteView_CountryCode
    ON perf.QuoteView (CountryCode);
```

`UNIQUE` on the clustered key avoids the hidden 4-byte uniquifier, which would
be copied into every non-clustered index row. It is created first because adding
a clustered index rewrites the row locator in every non-clustered index.
`ViewId DESC` is in the key, not the `INCLUDE` list, because Q2 sorts by it — a
tiebreaker outside the key order would force a `Sort`.

| Index | Pages | Size |
|---|---|---|
| `CIX_QuoteView_ViewedAt` | 1,994 | 15.58 MB |
| `IX_QuoteView_QuoteId_ViewedAt` | 390 | 3.05 MB |
| `IX_QuoteView_CountryCode` | 294 | 2.30 MB |

## Logical reads, before and after each index

| Query | Rows | Heap | After its index |
|---|---|---|---|
| Q1 one day of views, range on `ViewedAt` | 1 (over 1,108) | 1,956 | **25** |
| Q2 ten most recent views of quote 42 | 10 | 1,956 | **3** |
| Q3 views from country `MT` | 26 | 1,956 | **92** |

All three heap numbers are the heap's page count: a heap can only answer a
query by reading all of it, so the cost is a property of the table rather than
of the question.

## Plans

| Query | Operator | Rows | Executes |
|---|---|---|---|
| Q1 | `Clustered Index Seek(CIX_QuoteView_ViewedAt)`, ORDERED FORWARD | 1,108 | 1 |
| Q2 | `Top` over `Index Seek(IX_QuoteView_QuoteId_ViewedAt)`, ORDERED FORWARD | 10 | 1 |
| Q3 | `Index Seek(IX_QuoteView_CountryCode)` → `Nested Loops` → `Clustered Index Seek … LOOKUP` | 26 | **26** |

No `Sort` in any of the three. Q1 estimated 1,108.66 against an actual 1,108.
`Executes = 26` on Q3's lookup is one round trip to the clustered index per
matched row, because `QuoteId` and `DwellSeconds` are not in the index — cheap
at 26 rows, and the reason it is the right plan here. On a common value such as
`IN`, which matches over half the table, the optimiser declines the same index
and scans instead.

## Write cost

The same 20,000-row insert generated **2.00× the transaction log** with the three
indexes in place — 4,965 KB against 9,941 KB — because each row became three
index entries. Measured from
`sys.dm_tran_database_transactions.database_transaction_log_bytes_used` inside
each transaction, not elapsed time.
