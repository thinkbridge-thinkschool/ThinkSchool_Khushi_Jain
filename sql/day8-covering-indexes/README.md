# Day 8 — covering indexes and included columns

## The exercise

A covering index serves a query entirely from the index, avoiding the key lookup.
Take a query doing a key lookup, add an index with `INCLUDE`d columns to
eliminate it, and prove it from the plan. Submit the before plan showing the key
lookup, the index with `INCLUDE`, the after plan with the lookup gone, and the
logical-reads delta.

Database: `QuotesLab` on SQL Server 2022, and specifically the `perf.QuoteView`
table built for [piece 1](../day8-indexes/README.md) — 100,000 rows, 150.87 bytes
a row, 1,948 clustered pages. See [sql/README.md](../README.md) for how to run it.

| File | What it does | Output |
|---|---|---|
| [`13_covering_index_lab.sql`](13_covering_index_lab.sql) | Before plan, the `INCLUDE` index, after plan, the delta | [`results/13_covering_index_lab.txt`](results/13_covering_index_lab.txt) |
| [`14_include_tradeoffs.sql`](14_include_tradeoffs.sql) | `INCLUDE` vs key columns, what un-covers an index, `RID Lookup`, the width cost | [`results/14_include_tradeoffs.txt`](results/14_include_tradeoffs.txt) |

## The headline

| | Before | After |
|---|---|---|
| Logical reads on `perf.QuoteView` | **696** | **16** |
| Key lookups | **219** | **0** |
| Plan | `Index Seek` → `Nested Loops` → `Clustered Index Seek … LOOKUP` | `Index Seek` |

43.5× fewer reads, from three column names added to an `INCLUDE` list. Not one
character of the query changed.

---

## Getting a key lookup to happen is the hard part

The exercise says "take a query doing a key lookup", as though the hard part were
the fix. On this table the hard part is the *before* state, and understanding why
is worth more than the fix is.

**A key lookup is only ever the optimiser's choice inside a window.** Too few
matching rows and there is nothing to measure. Too many and reading the clustered
index end to end is cheaper, so the index is abandoned and there is no lookup to
eliminate. The clustered index is 1,948 pages and three levels deep, so one lookup
costs about three reads and the two plans cross near **649 rows**. Piece 1
section 7a measured the far side of that cliff at 88×.

Two further escape routes have to be closed, and both are properties of the
clustered key rather than of the query:

- **No `TOP` with an `ORDER BY` on `ViewedAt`.** The clustered index is ordered on
  `ViewedAt` and can be read backwards, so a `TOP`-N query terminates early — it
  reads a few hundred pages and stops, which beats a few hundred random lookups.
  Piece 1's Q2 showed this happening for free at stage 1.
- **No bound on `ViewedAt`.** It is the leading clustered column, so any
  time-bounded predicate can be served by a clustered range seek, and the lookup
  plan loses again.

Which rules out the obvious candidate. `QuoteId` is the column piece 1 indexed,
and "the 200 newest views of quote 42, by device and country" *looks* like it must
key-lookup. It does not: the optimiser answers it with
`Clustered Index Scan … ORDERED BACKWARD` and a residual predicate on `QuoteId`,
reading 346 pages and stopping, against ~600 for 200 lookups. **On this table no
`TOP`-N query on `QuoteId` can be made to key-lookup**, because `ViewedAt` orders
the table and `TOP` lets that ordering pay off. That is a fact about the fixture,
and it is the kind of thing only a measurement tells you.

So the predicate has to be an equality on a column that is neither the clustered
key nor correlated with it, selective enough to stay under the cliff, with no
`TOP` and no time bound. `UserId` is that column: 2,500 accounts at roughly forty
views each, drawn from an independent hash slice, so it has no relationship to
insert order.

Section 1a prints the arithmetic rather than asserting it:

```
MatchedRows|ClusteredPages|CliffAtRoughly
-----------|--------------|--------------
219|1948|649
```

219 rows, a third of the way to the cliff. Comfortable margin, and stated up front
so a reader can check the design rather than trust it.

---

## The starting index, which is not a strawman

```sql
CREATE NONCLUSTERED INDEX IX_QuoteView_UserId_ViewedAt
    ON perf.QuoteView (UserId, ViewedAt DESC, ViewId DESC);
```

This is the index anyone would write for "this account's views, newest first" —
equality on the account, then the timeline in the direction it gets read. It is
the same shape piece 1 chose for `QuoteId`, for the same reasons. 341 pages,
2.66 MB, 23 bytes a leaf row.

What it does not have is a projection. It finds rows beautifully and can produce
almost nothing about them, and that is the ordinary way a covering problem
arrives: not from a bad index, but from a good index and a `SELECT` list that grew
after it was designed.

## The before query

```sql
SELECT
    v.DeviceType,          -- not in the index
    v.CountryCode,         -- not in the index
    Views    = COUNT(*),
    AvgDwell = CONVERT(decimal(8,2), AVG(v.DwellSeconds * 1.0))   -- not in the index
FROM perf.QuoteView AS v
WHERE v.UserId IN (137, 842, 1699, 2001, 2444)
GROUP BY v.DeviceType, v.CountryCode
ORDER BY Views DESC, v.DeviceType, v.CountryCode
OPTION (MAXDOP 1);
```

An account-activity summary. The predicate is served by the index; all three
projected columns are not. The `GROUP BY` means the whole matched set has to be
read, so no plan can stop early.

---

## Where the key lookup is, in a text plan

**In a text plan a key lookup is not labelled "Key Lookup"** — that is the name
graphical plans use. In `SHOWPLAN` and `STATISTICS PROFILE` output it is a
Clustered Index Seek whose `Argument` carries the keyword `LOOKUP` and whose seek
predicate is the entire clustering key. `LOOKUP` is the tell and `Executes` is the
number of round trips.

Because reading that out of a 20-column text plan is a judgement call, the scripts
also **count** the lookups.
`sys.dm_db_index_operational_stats.singleton_lookup_count` for the clustered index
is exactly the number of key lookups performed. It is what caught the first,
flawed version of this piece: the before plan reported `BeforeLookups = 0`, which
no amount of squinting at a plan tree would have made obvious.

---

## Before plan

Verbatim from [`results/13_covering_index_lab.txt`](results/13_covering_index_lab.txt)
section 4, trimmed to the operators that matter:

```
Rows|Executes|StmtText
219|1|      |--Nested Loops(Inner Join, OUTER REFERENCES:([v].[ViewId], [v].[ViewedAt], [Expr1011]) WITH UNORDERED PREFETCH)
219|1|           |--Index Seek(OBJECT:([QuotesLab].[perf].[QuoteView].[IX_QuoteView_UserId_ViewedAt] AS [v]),
                     SEEK:([v].[UserId]=(137) OR [v].[UserId]=(842) OR [v].[UserId]=(1699)
                        OR [v].[UserId]=(2001) OR [v].[UserId]=(2444)) ORDERED FORWARD)
219|219|         |--Clustered Index Seek(OBJECT:([QuotesLab].[perf].[QuoteView].[CIX_QuoteView_ViewedAt] AS [v]),
                     SEEK:([v].[ViewedAt]=[...].[ViewedAt] AND [v].[ViewId]=[...].[ViewId]) LOOKUP ORDERED FORWARD)
```

```
Table 'QuoteView'. Scan count 5, logical reads 696
```

`Executes = 219` on the `LOOKUP` seek — one round trip per matched row, and
exactly the `MatchedRows` figure from section 1a. The index seek finds all 219
rows in five range scans for almost nothing; the 219 individual trips back to the
clustered index are the whole cost. Note also what the seek's `OutputList` carries:
`[v].[ViewId], [v].[ViewedAt]` and nothing else. The index has the row locator and
none of the data.

---

## The index with `INCLUDE`

```sql
CREATE NONCLUSTERED INDEX IX_QuoteView_UserId_ViewedAt_Covering
    ON perf.QuoteView (UserId, ViewedAt DESC, ViewId DESC)
    INCLUDE (DeviceType, CountryCode, DwellSeconds);
```

The key is unchanged, character for character. Only the leaf gets wider. That is
the whole intervention — the seek and the ordering were already right, and the
query was leaving the index for three columns it could have been handed.

All three go in `INCLUDE` rather than in the key because the query neither
searches nor sorts by them: it groups by two and averages the third, after the
rows have already been found and ordered.

---

## After plan

Same file, section 7:

```
Rows|Executes|StmtText
219|1|      |--Index Seek(OBJECT:([QuotesLab].[perf].[QuoteView].[IX_QuoteView_UserId_ViewedAt_Covering] AS [v]),
                SEEK:([v].[UserId]=(137) OR [v].[UserId]=(842) OR [v].[UserId]=(1699)
                   OR [v].[UserId]=(2001) OR [v].[UserId]=(2444)) ORDERED FORWARD)
```

```
Table 'QuoteView'. Scan count 5, logical reads 16
```

The `Nested Loops` and the `LOOKUP` seek are **absent**, not cheaper. One operator
where there were three. `CIX_QuoteView_ViewedAt` is not touched at all, which is
what "served entirely from the index" means — and the seek's `OutputList` now
carries `[v].[CountryCode], [v].[DeviceType], [v].[DwellSeconds]`, the three
columns that used to require a trip to the table.

The estimated subtree cost falls from 0.666 to 0.0297, a 22× drop, which is the
optimiser agreeing with the measurement.

---

## The logical-reads delta

```
QueryName|BeforeReads|AfterReads|ReadsSaved|BeforeLookups|AfterLookups|TimesCheaper
---------|-----------|----------|----------|-------------|------------|------------
Q6 account activity mix|716|16|700|219|0|44.75
```

`BeforeLookups` 219 and `AfterLookups` 0. Those two integers are the proof the
exercise is really asking for; the reads delta is the consequence.

The `SET STATISTICS IO` figures are 696 → 16, a 43.5× cut. The grid reads 716 for
the before case because the counter is per *request* and picks up ~20 pages of
other work; the per-table figure is the record.

**On the harness.** Piece 1's summary grid read 20–40 pages high on every query,
because each measured query carried `OPTION (RECOMPILE)` and the catalog pages
read to compile a plan landed inside the measurement. That is noise against a
1,900-page scan and would have been most of the number against a 16-page covering
seek. `perf.MeasureCovering` therefore runs each query twice and measures the
second pass: creating or dropping an index invalidates every cached plan for the
table, so the priming pass recompiles against the current index set and the
measured pass reuses that plan. The queries carry `MAXDOP 1` but not `RECOMPILE`.
The residual 20-page gap above is that fix working as far as it goes and not
further.

### What it cost in space

```
IndexName|IndexLevel|page_count|record_count|AvgRecordBytes|AvgPageFullnessPct
---------|----------|----------|------------|--------------|------------------
IX_QuoteView_UserId_ViewedAt|0|320|100000|23.00|96.50
IX_QuoteView_UserId_ViewedAt|1|3|320|26.00|36.88
IX_QuoteView_UserId_ViewedAt|2|1|3|26.00|1.01
IX_QuoteView_UserId_ViewedAt_Covering|0|518|100000|39.34|98.58
IX_QuoteView_UserId_ViewedAt_Covering|1|8|518|26.00|22.37
IX_QuoteView_UserId_ViewedAt_Covering|2|1|8|26.00|2.74
```

Identical keys, identical row counts, so the leaf record growing 23.00 → 39.34
bytes is exactly what the three included columns add to every row: **16.34 bytes,
paid 100,000 times.** Leaf pages 320 → 518, and the whole index 341 → 544 pages
(2.66 → 4.25 MB), a 60% increase to remove 219 lookups from one query.

And the non-leaf record size is **26.00 bytes in both**. That is `INCLUDE` doing
precisely what it claims: the extra columns are in the leaf and nowhere else.

---

## `INCLUDE` against putting the same columns in the key

Both of these cover the query. They differ only in where three columns live:

```sql
-- included
ON perf.QuoteView (UserId, ViewedAt DESC, ViewId DESC)
   INCLUDE (DeviceType, CountryCode, DwellSeconds);

-- keyed
ON perf.QuoteView (UserId, ViewedAt DESC, ViewId DESC,
                   DeviceType, CountryCode, DwellSeconds);
```

```
IndexName|IndexLevel|LevelKind|page_count|record_count|AvgRecordBytes|PageFullnessPct
---------|----------|---------|----------|------------|--------------|---------------
IX_Cover_KeyedColumns|0|leaf|513|100000|39.34|99.54
IX_Cover_KeyedColumns|1|non-leaf|3|513|42.36|93.69
IX_Cover_KeyedColumns|2|non-leaf|1|3|42.00|1.61
IX_QuoteView_UserId_ViewedAt_Covering|0|leaf|518|100000|39.34|98.58
IX_QuoteView_UserId_ViewedAt_Covering|1|non-leaf|8|518|26.00|22.37
IX_QuoteView_UserId_ViewedAt_Covering|2|non-leaf|1|8|26.00|2.74
```

The leaf record sizes agree **exactly** — 39.34 both — which is the point: the two
indexes store the same data at the bottom. The non-leaf record sizes do not:
26.00 for the `INCLUDE` version against 42.36 for the keyed one. That 16.36-byte
difference is `DeviceType`, `CountryCode` and `DwellSeconds` being carried through
every level of a B-tree that has no use for them. It is the structural fact, and
it is the thing that grows with the table.

**But do not read the page counts as the comparison, because they run the wrong
way.** The `INCLUDE` version holds *eight* non-leaf pages to the keyed version's
*three*, despite rows 39% smaller, because its upper-level pages came out 22% full
against 94%. At 100,000 rows the non-leaf levels are a handful of pages and their
fill varies enough between builds to swamp a 16-byte row-width difference
entirely. The naive "smaller rows, therefore fewer pages" reading is simply wrong
at this size, and saying so is more useful than a tidy number would have been.

The differences that are not about size, and that this fixture cannot
demonstrate:

| | key columns | `INCLUDE`d columns |
|---|---|---|
| Ordered | yes — seekable, satisfies `ORDER BY` | no — projectable only |
| Stored in | every level of the B-tree | the leaf only |
| Counts against the 1,700-byte / 32-column key limit | yes | no |
| Types allowed | only key-legal types | also `nvarchar(max)`, `varbinary(max)`, `xml` |

That last row is the case where `INCLUDE` is not an optimisation but the only
option: an `nvarchar(max)` column cannot be part of a key at all, so covering a
query that projects one is impossible any other way.

---

## The one-line edit that un-covers a covering index

`Q7` is the same query with one column added to the projection — `Referrer`, for a
distinct-referrer count. Same predicate, same grouping, same ordering.

```
Stage|QueryName|LogicalReads|KeyLookups
-----|---------|------------|----------
before|Q6 account activity mix|716|219
after|Q6 account activity mix|16|0
un-covered|Q7 same query plus Referrer|716|219
re-covered|Q7 against the fat index|23|0
```

**716 and 219, exactly the before numbers.** The covering index is still there. It
is still the right index. It no longer covers, so all 219 lookups return and the
tuning is worth nothing.

**This is the finding I would carry out of this exercise.** No index "is a
covering index". Covering is a property of the *(query, index) pair*, and the query
half is the half that changes — a column added to a dashboard six months later
widens a projection, and nothing fails, nothing warns, and the plan quietly
reverts to what it was before anyone tuned it.

## Where widening stops paying

Section 4 does the obvious thing: includes `Referrer` and `UserAgent` too, both
`varchar(120)`, both copied into all 100,000 leaf rows. It does cover `Q7` — 716
down to 23 reads. And it costs this:

```
IndexName|KeyColumns|IncludedCols|UsedPages|UsedKB|LeafPages|AvgLeafBytes
---------|----------|------------|---------|------|---------|------------
IX_QuoteView_CountryCode|CountryCode|-|295|2360|287|21.00
IX_QuoteView_UserId_ViewedAt|UserId, ViewedAt DESC, ViewId DESC|-|341|2728|320|23.00
IX_QuoteView_QuoteId_ViewedAt|QuoteId, ViewedAt DESC, ViewId DESC|DwellSeconds|390|3120|367|27.00
IX_Cover_KeyedColumns|UserId, ViewedAt DESC, ViewId DESC, DeviceType, CountryCode, DwellSeconds|-|518|4144|513|39.34
IX_QuoteView_UserId_ViewedAt_Covering|UserId, ViewedAt DESC, ViewId DESC|CountryCode, DeviceType, DwellSeconds|544|4352|518|39.34
IX_Cover_Fat|UserId, ViewedAt DESC, ViewId DESC|CountryCode, DeviceType, DwellSeconds, Referrer, UserAgent|1850|14800|1817|143.27
```

**The fat index is 1,850 pages against the clustered table's 1,948.** Its leaf rows
are 143.27 bytes against the table's 150.87. Two `varchar(120)` columns in an
`INCLUDE` list turn an index into a 95%-scale copy of the table — a second table,
maintained on every write, to save one query 693 logical reads.

Set against the lean covering index at 544 pages doing the same job for `Q6`, the
curve is stark: the first three included columns cost 203 pages and removed 219
lookups. The next two cost **1,306 more pages** and removed 219 lookups from one
further query. That is where widening stops paying, and it is a number rather than
an opinion.

---

## `RID Lookup` — the same operator on a heap

A non-clustered index stores a row locator, and what that locator *is* depends on
the table. On a clustered table it is a copy of the clustering key. On a heap it is
an 8-byte physical row id, and following it is a `RID Lookup` — which, unlike the
clustered form, does appear in a text plan under that name:

```
10|1|       |--Nested Loops(Inner Join, OUTER REFERENCES:([Bmk1000]))
10|1|            |--Index Seek(OBJECT:([QuotesLab].[perf].[CoverHeap].[IX_CoverHeap_CountryCode] AS [v]),
                     SEEK:([v].[CountryCode]='MT') ORDERED FORWARD)
10|10|           |--RID Lookup(OBJECT:([QuotesLab].[perf].[CoverHeap] AS [v]),
                     SEEK:([Bmk1000]=[Bmk1000]) LOOKUP ORDERED FORWARD)
```

`[Bmk1000]` is the bookmark — the row id itself, visible in the plan in a way the
clustering key never is. `Executes = 10`, one per matched row. Covering it drops
`CoverHeap` from 12 logical reads to 2 and leaves a bare `Index Seek`.

There is a real asymmetry here, and it is why the heap's covering index needs a
longer `INCLUDE` list. Covering an index on a clustered table gets the clustering
key thrown in for free — it is already in the leaf as the row locator, so
`ViewedAt` and `ViewId` cost nothing to project. A heap has no clustering key, so
every projected column has to be named: `INCLUDE (ViewId, ViewedAt, DeviceType,
DwellSeconds)` where the clustered version needed only two of those.

---

## What I would keep

Covering is a property of the pair, not the index. `IX_QuoteView_UserId_ViewedAt`
serves the predicate and the ordering of this query perfectly and forces 219 round
trips anyway, and the fix is three column names rather than anything about the
query. Adding one column to the `SELECT` list undoes it completely and silently.

The corollary is where to look first when a query that used to be fast is not. Not
the `WHERE` clause, which is where index tuning instinctively goes — the `SELECT`
list, which is where the covering property actually lives and which nobody reviews
as if it were performance-critical.

The second thing I would keep is smaller and more practical: **count the lookups,
do not read them off the plan.** `singleton_lookup_count` is one join away and it
is unambiguous. Judging a wide text plan by eye is how a before-state with zero key
lookups in it gets mistaken for one that has 219.

---

## What would break this

**A column added to the `SELECT` list.** Measured, not asserted: 716 reads and 219
lookups, identical to the untuned state. Nothing errors.

**The window this exercise lives in is narrow, and it is a property of the table.**
Above ~649 matched rows the optimiser abandons the index and scans, so there is no
lookup to eliminate. Add a `TOP` with an `ORDER BY` on `ViewedAt` and the clustered
index scans backwards and terminates early, which also beats the lookup plan. Bound
`ViewedAt` at all and the clustered index range-seeks instead. Three ways for the
before state to evaporate, none visible in the query text.

**The fix has a fixed cost and a variable benefit.** The 16.34 bytes a row are paid
on every insert, update and delete forever; the benefit is paid out only when this
query runs. Piece 1 measured the write side of that trade on this same table at
2.2× the transaction log for two non-clustered indexes and 3.8× for updating a
column that is an index key. A third index at 544 pages is not free, and
`IX_Cover_Fat` at 1,850 pages would be a serious decision.

**The table is 15 MB, so every number here is a warm-cache measurement** —
`physical reads 0` throughout. Logical reads are the right metric because they do
not care, but 219 lookups against a table that does not fit in memory is 219 random
physical reads, and that is a different problem wearing the same plan shape.

**`IX_Cover_KeyedColumns` came out smaller than the `INCLUDE` version** — 518 pages
against 544 — which is the opposite of the tidy conclusion and is reported above
rather than smoothed over. At this scale non-leaf page fill dominates a 16-byte row
difference. The row width is the fact that scales; the page count at 100,000 rows
is not a clean comparison.

**The final state of `perf.QuoteView` is piece 1's three indexes.**
`14_include_tradeoffs.sql` drops everything this piece creates, confirmed in its
section 5b. In production the covering index would *replace*
`IX_QuoteView_UserId_ViewedAt` rather than sit beside it — the keys are identical,
so the narrow one answers nothing the wide one cannot, and keeping both means two
index writes per insert to serve one read path. Both are dropped here because two
submitted pieces share one database and the earlier one's captured evidence has to
stay reproducible.
