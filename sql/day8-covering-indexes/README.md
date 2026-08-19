# Day 8 — covering indexes and included columns

Take a query doing a key lookup, add an index with `INCLUDE`d columns to
eliminate it, and prove it from the plan.

Script: [`covering_index_lab.sql`](covering_index_lab.sql). Captured run:
[`results/covering_index_lab.txt`](results/covering_index_lab.txt). Runs after
[piece 1](../day8-indexes/README.md), which builds `perf.QuoteView`.

## Result

| | Before | After |
|---|---|---|
| Logical reads | **694** | **16** |
| Key lookups | **219** | **0** |
| Plan | `Index Seek` → `Nested Loops` → `Clustered Index Seek … LOOKUP` | `Index Seek` |

43× fewer reads from three column names, with no change to the query.

## The starting index

```sql
CREATE NONCLUSTERED INDEX IX_QuoteView_UserId_ViewedAt
    ON perf.QuoteView (UserId, ViewedAt DESC, ViewId DESC);
```

The index anyone would write for "this account's views, newest first". It serves
the predicate and the ordering, and holds none of the columns the query returns.

The query groups five accounts' views by device and country. `UserId` is used
because it is neither the clustered key nor correlated with it; there is no `TOP`
and no bound on `ViewedAt`, either of which would let the clustered index answer
the query directly and leave no lookup to remove. 219 rows match.

## Before

```
219|1|  |--Nested Loops(Inner Join, ...)
219|1|       |--Index Seek(OBJECT:(...IX_QuoteView_UserId_ViewedAt), SEEK:(...UserId=(137) OR ...) ORDERED FORWARD)
219|219|     |--Clustered Index Seek(OBJECT:(...CIX_QuoteView_ViewedAt), SEEK:(...) LOOKUP ORDERED FORWARD)
```

```
Table 'QuoteView'. Scan count 5, logical reads 694
```

In a text plan a key lookup is not labelled "Key Lookup" — that is the graphical
name. It is a `Clustered Index Seek` whose `Argument` carries `LOOKUP` and whose
seek predicate is the whole clustering key. `Executes = 219` is the number of
round trips: one per matched row.

## The covering index

```sql
CREATE NONCLUSTERED INDEX IX_QuoteView_UserId_ViewedAt_Covering
    ON perf.QuoteView (UserId, ViewedAt DESC, ViewId DESC)
    INCLUDE (DeviceType, CountryCode, DwellSeconds);
```

The key is unchanged. All three columns go in `INCLUDE` because the query groups
by two and averages the third — it never seeks or sorts by them.

## After

```
219|1|  |--Index Seek(OBJECT:(...IX_QuoteView_UserId_ViewedAt_Covering), SEEK:(...) ORDERED FORWARD)
```

```
Table 'QuoteView'. Scan count 5, logical reads 16
```

The `Nested Loops` and the `LOOKUP` seek are absent, not cheaper, and
`CIX_QuoteView_ViewedAt` is not touched at all. Estimated subtree cost falls from
0.667 to 0.030.

## What the `INCLUDE` cost

| Index | Level | Pages | Record bytes |
|---|---|---|---|
| `IX_QuoteView_UserId_ViewedAt` | 0 (leaf) | 320 | 23.00 |
| `IX_QuoteView_UserId_ViewedAt` | 1 | 3 | 26.00 |
| `IX_QuoteView_UserId_ViewedAt_Covering` | 0 (leaf) | 517 | 39.33 |
| `IX_QuoteView_UserId_ViewedAt_Covering` | 1 | 8 | 26.00 |

Identical keys and row counts, so the leaf record growing 23.00 → 39.33 bytes is
exactly what the three included columns add to every row, paid 100,000 times.
The non-leaf record is **26.00 bytes in both**: included columns live in the leaf
and nowhere else, which is the difference between `INCLUDE` and adding the same
columns to the key.

Both indexes are dropped at the end of the script, leaving the table in the
three-index state piece 1 captured its plans against.
