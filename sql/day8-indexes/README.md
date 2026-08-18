# Day 8 — clustered vs non-clustered indexes

## The exercise

Create a clustered index and two non-clustered indexes on a table with ~100k
rows, generating the data. Use `SET STATISTICS IO ON` and the actual execution
plan. Submit the index DDL, a query that uses each, the logical reads before and
after each index, and one line on the write-side cost.

Database: `QuotesLab` on SQL Server 2022, the same one built for the
[Day 7 pieces](../day7-joins-and-ctes/README.md). See [sql/README.md](../README.md)
for how to run it.

| File | What it does | Output |
|---|---|---|
| [`09_build_dataset.sql`](09_build_dataset.sql) | Creates `perf.QuoteView` as a **heap**, generates 100,000 rows | [`results/09_build_dataset.txt`](results/09_build_dataset.txt) |
| [`10_index_lab.sql`](10_index_lab.sql) | The staircase: measure, add an index, measure again, four times | [`results/10_index_lab.txt`](results/10_index_lab.txt) |
| [`11_actual_plans.sql`](11_actual_plans.sql) | The actual execution plans behind those numbers | [`results/11_actual_plans.txt`](results/11_actual_plans.txt) |
| [`12_write_cost.sql`](12_write_cost.sql) | Four identical tables, different indexes, same 20,000 inserts | [`results/12_write_cost.txt`](results/12_write_cost.txt) |

The dataset: 100,000 rows, 150.87 bytes a row, 1,906 pages at 99% full — about
15 MB. 80 distinct quotes, 2,500 users, 90 days of history, and a deliberately
skewed `CountryCode`: `IN` on 55,417 rows (55.4%) down to `MT` on 26 (0.026%).

---

## The index DDL

```sql
-- The clustered index. It is not a structure beside the table, it is the table:
-- the leaf level holds the rows, in key order. Hence one per table.
CREATE UNIQUE CLUSTERED INDEX CIX_QuoteView_ViewedAt
    ON perf.QuoteView (ViewedAt, ViewId);

-- Non-clustered #1: covering, for the per-quote reads.
CREATE NONCLUSTERED INDEX IX_QuoteView_QuoteId_ViewedAt
    ON perf.QuoteView (QuoteId, ViewedAt DESC, ViewId DESC)
    INCLUDE (DwellSeconds);

-- Non-clustered #2: narrow, and deliberately not covering.
CREATE NONCLUSTERED INDEX IX_QuoteView_CountryCode
    ON perf.QuoteView (CountryCode);
```

**Why `(ViewedAt, ViewId)` is clustered.** It is ever-increasing, so every insert
lands at the right-hand end and pages fill rather than split — measured below at
98.8% full and 4.7% fragmented. It is the dominant predicate: practically every
question asked of an event log is bounded by time, and only the clustered index
can serve a time range without a lookup per row, because the row is already
there. And it is narrow at 15 bytes, which matters more than it looks — the
clustering key is copied into every row of every non-clustered index.

**Why `UNIQUE`.** `ViewedAt` alone is not unique in general, and a non-unique
clustered index makes SQL Server append a hidden 4-byte uniquifier to duplicate
keys, carried into every non-clustered index row. `ViewId` is unique by
construction, so it buys the same guarantee with bytes that do something.

**Why `INCLUDE (DwellSeconds)` and not a fourth key column.** Q3 aggregates
`DwellSeconds`; it never searches or sorts by it. Included columns live only in
the leaf, so they widen the index slightly and change neither its depth nor its
ordering.

**Why `ViewId DESC` is in the key of #1.** Q2 orders by `ViewedAt DESC, ViewId
DESC`. A tiebreaker outside the key order is still correct and still forces a
Sort over all 1,268 rows of the seek — the same point
[Day 7's plans file](../day7-joins-and-ctes/06_plans.sql) made about
`IX_Quote_Author_CreatedAt`. The captured plans have no Sort in any of the five.

**And the clustered index is created first.** A non-clustered index stores a row
locator per row: on a heap an 8-byte RID, on a clustered table a copy of the
clustering key. Adding or dropping a clustered index therefore rewrites every
non-clustered index on the table.

What the three cost in space:

| Index | Pages | Size |
|---|---|---|
| `CIX_QuoteView_ViewedAt` | 1,948 | 15.22 MB |
| `IX_QuoteView_QuoteId_ViewedAt` | 390 | 3.05 MB |
| `IX_QuoteView_CountryCode` | 295 | 2.30 MB |
| **total** | **2,633** | **20.57 MB** |

Against the 1,907-page heap: **38% more storage** for the same 100,000 rows.

---

## Method

Five queries, written once as procedures, run in full at every stage: heap →
`+ clustered` → `+ NC1` → `+ NC2`. Running all five at every stage costs nothing
and answers the question the exercise does not ask — what an index does to the
queries it was *not* built for.

**Logical reads, not elapsed time.** A logical read is one 8KB page handed to the
query, whether from the buffer pool or from disk, so the count is a property of
the query and the indexes and nothing else. Elapsed time on the same query varies
with cache state and with what else is on the machine.

**`OPTION (MAXDOP 1, RECOMPILE)` on every measured query** — `MAXDOP 1` so a plan
that goes parallel on one machine and serial on another does not report different
work for the same query, `RECOMPILE` so each stage is planned against the indexes
that exist at that stage.

**No parameters on any procedure.** With a parameter, the plan cached for
`CountryCode = 'MT'` would be reused for `'IN'`, and the tipping point below
would be hidden behind parameter sniffing.

**Statistics created with `FULLSCAN` before anything is measured.** Not
decoration: with `AUTO_CREATE_STATISTICS` on, the first query to filter on
`CountryCode` builds that statistic during its own execution and the reads land
in that query's count, inflating the heap baseline with work that has nothing to
do with the absence of an index. It also paid off in the plans — Q1's estimate
came in at 1,108.66 rows against an actual 1,108.

---

## The five queries

**Q1 — one day of views.** `ViewedAt` is the leading clustered column.

```sql
SELECT
    ReportDay = CONVERT(date, '2026-02-01'),
    ViewCount = COUNT(*),
    AvgDwell  = CONVERT(decimal(8,2), AVG(v.DwellSeconds * 1.0)),
    MaxDwell  = MAX(v.DwellSeconds)
FROM perf.QuoteView AS v
WHERE v.ViewedAt >= '2026-02-01T00:00:00'
  AND v.ViewedAt <  '2026-02-02T00:00:00'
OPTION (MAXDOP 1, RECOMPILE);
```

Half-open, not `BETWEEN`. `ViewedAt` is `datetime2(3)`, so `BETWEEN '2026-02-01'
AND '2026-02-02'` would include midnight of the second day and double-count it
against the neighbouring day's report.

**Q2 — the ten most recent views of one quote.** Non-clustered #1: seek on
`QuoteId`, newest first, `DwellSeconds` from the leaf.

```sql
SELECT TOP (10) v.QuoteId, v.ViewedAt, v.DwellSeconds
FROM perf.QuoteView AS v
WHERE v.QuoteId = 42
ORDER BY v.ViewedAt DESC, v.ViewId DESC
OPTION (MAXDOP 1, RECOMPILE);
```

**Q3 — dwell rollup for three quotes.** Also #1, and the query that pays for the
`INCLUDE`: 3,800 rows read, three returned.

```sql
SELECT v.QuoteId, ViewCount = COUNT(*),
       AvgDwell = CONVERT(decimal(8,2), AVG(v.DwellSeconds * 1.0))
FROM perf.QuoteView AS v
WHERE v.QuoteId IN (7, 42, 63)
GROUP BY v.QuoteId
ORDER BY v.QuoteId
OPTION (MAXDOP 1, RECOMPILE);
```

**Q4 — detail rows for a rare country.** Non-clustered #2. `QuoteId` and
`DwellSeconds` are not in that index, so this is a seek plus one key lookup per
matched row — and 26 lookups is nothing, which is when a narrow non-covering
index is right.

```sql
SELECT v.ViewId, v.ViewedAt, v.QuoteId, v.CountryCode, v.DwellSeconds
FROM perf.QuoteView AS v
WHERE v.CountryCode = 'MT'
ORDER BY v.ViewedAt, v.ViewId
OPTION (MAXDOP 1, RECOMPILE);
```

No `TOP`. Capping it would let the heap scan stop early once it had found enough
rows, measuring less work in the "before" column than the indexed version does.

**Q5 — the same shape, on the commonest country.** Same column, same index, and
the right plan is the opposite one.

```sql
SELECT CountryCode = 'IN', ViewCount = COUNT(*),
       AvgDwell = CONVERT(decimal(8,2), AVG(v.DwellSeconds * 1.0))
FROM perf.QuoteView AS v
WHERE v.CountryCode = 'IN'
OPTION (MAXDOP 1, RECOMPILE);
```

---

## Logical reads, before and after each index

`SET STATISTICS IO ON` throughout, reading the `perf.QuoteView` line at each
stage. Verbatim from [`results/10_index_lab.txt`](results/10_index_lab.txt):

| Query | rows returned | Heap | + clustered | + NC1 | + NC2 |
|---|---|---|---|---|---|
| Q1 one day | 1 (over 1,108) | 1906 | **26** | 26 | 26 |
| Q2 recent for one quote | 10 | 1906 | 37 | **3** | 3 |
| Q3 rollup, 3 quotes | 3 (over 3,800) | 1906 | 1932 | **26** | 26 |
| Q4 rare country `MT` | 26 | 1906 | 1932 | 1932 | **90** |
| Q5 common country `IN` | 1 (over 55,417) | 1906 | 1932 | 1932 | 1932 |

Six things in that table, in ascending order of how much they taught me:

**All five heap numbers are identical at 1,906** — exactly the heap's page count.
A heap has no order and no navigation structure, so there is one way to satisfy
any predicate: read every page. The cost of a heap query is a property of the
table, not of the question. Q5 reads the same 1,906 pages to return one row as Q4
does to return 26.

**Q1 collapses at stage 1 and nowhere else**, 1906 → 26, a 73× cut. A range seek
over the 1,108 rows that match instead of 100,000 rows discarded one at a time.

**Q2 and Q3 collapse at stage 2 and nowhere else**, and Q2 further: a `TOP (10)`
over an ordered seek reads three pages, while Q3 must read all 3,800 matching
index rows to aggregate them. Neither touches the table.

**Three cells got *worse* than the heap** — 1932 against 1906. The clustered
index is 1,948 pages against the heap's 1,907, so a query that has to scan
anyway pays about 26 extra pages for the privilege of being in order. Small, but
it is the honest direction of the effect and it is why "add a clustered index" is
not free even for reads.

**Q2 at stage 1 is 37, not 1932.** This one I did not predict. The clustered index
is ordered on `ViewedAt` and readable backwards, `QuoteId = 42` matches about one
row in 80, and `TOP (10)` lets the scan stop as soon as it has ten — so it reads
roughly 800 rows instead of 100,000. A clustered index helping a query it was not
designed for, purely because `TOP` can terminate early. Nothing about the index
definition suggests it; only the actual plan and the row count show it.

**Q5 never improves.** 1932 at every indexed stage, and 0.99× the heap baseline
in the final state. That is the most useful line on this page.

### The convenience grid, and why it reads high

Section 6a of the same file prints the whole matrix in one place, captured by
differencing `sys.dm_exec_requests.logical_reads` around each call:

```
QueryName|Heap|PlusClustered|PlusNC1|PlusNC2
---------|----|-------------|-------|-------
Q1 one day|1948|52|50|50
Q2 recent for one|1946|57|27|25
Q3 rollup 3 quotes|1946|1956|56|56
Q4 rare country|1946|1952|1956|140
Q5 common country|1946|1952|1956|1962
```

Every figure sits 20–40 above the `STATISTICS IO` table. The counter is per
*request*, not per table, so it also counts the catalog pages read to compile the
query — and every measured query carries `OPTION (RECOMPILE)`, so each execution
recompiles and reads `sysschobjs`, `sysidxstats`, `syscolpars` and friends. That
is noise against a 1,900-page scan and most of the number against a three-page
covering seek. `STATISTICS IO` is the authoritative record; this grid is for
reading the shape at a glance. Section 6b states it as ratios: Q2 77.8× cheaper,
Q1 39.0×, Q3 34.8×, Q4 13.9×, Q5 **0.99×**.

---

## The index used correctly by one query and refused by another

`IX_QuoteView_CountryCode` is applicable to Q5 — an equality on its only key
column. It is available, it is a seventh the size of the table, and the optimiser
declined it, because 55,417 rows match and each would need a round trip to the
clustered index for `DwellSeconds`.

Section 7a forces the plan it refused, so the refusal can be priced rather than
trusted:

```sql
FROM perf.QuoteView AS v WITH (INDEX (IX_QuoteView_CountryCode), FORCESEEK)
```

**169,885 logical reads**, against 1,932 for the scan the optimiser chose. Same
answer, **88× the work**, from adding one hint to a query whose index was already
in place and already correct.

This is the failure mode worth naming because it survives review. The DDL is
right. The query is right. The index *is* used. And the report got 88× slower —
so "check that the index is being used" does not catch it, and nothing short of
measuring does. Selectivity is not a property of the column; it is a property of
the **value**, and the same index on the same column is a seek for `'MT'` and a
disaster for `'IN'`.

The fix is not a better predicate, it is `INCLUDE`:

| Plan for Q5 | Logical reads |
|---|---|
| forced seek + 55,417 key lookups | 169,885 |
| clustered index scan (what the optimiser picked) | 1,932 |
| seek on `(CountryCode) INCLUDE (DwellSeconds)` | **191** |

Section 7b creates that covering index, measures it, and drops it again so the
submitted DDL stays at three. In production it would *replace*
`IX_QuoteView_CountryCode` rather than join it: a covering index whose key is a
prefix of another index's key makes that other index redundant, and keeping both
means paying twice on every write for one answer.

Section 7c is the counterweight. `SELECT COUNT(*) FROM perf.QuoteView` names no
column at all, so any structure covering every row will do — and the optimiser
picks the smallest, reading **289 pages** off the 295-page `CountryCode` index
rather than 1,932 off the table. The same index that is a trap for Q5 is free
money for a query that never mentions the column.

---

## The actual execution plans

`SET STATISTICS PROFILE ON`, not `SET SHOWPLAN_TEXT ON`. `SHOWPLAN_TEXT` — which
[Day 7's `06_plans.sql`](../day7-joins-and-ctes/06_plans.sql) used — compiles a
query and returns the plan without running it, so every row count in it is a
guess. `STATISTICS PROFILE` executes and returns the plan actually used, with
`Rows` holding the rows that really flowed through each operator and `Executes`
holding how many times each ran. For the same plans graphically: Ctrl+M in SSMS
or Azure Data Studio, or `SET STATISTICS XML ON` and save the cell as `.sqlplan`.

| Query | Operator | Rows | Executes |
|---|---|---|---|
| Q1 | `Clustered Index Seek(CIX_QuoteView_ViewedAt)`, ORDERED FORWARD | 1,108 | 1 |
| Q2 | `Top` over `Index Seek(IX_QuoteView_QuoteId_ViewedAt)` | 10 | 1 |
| Q3 | `Index Seek(IX_QuoteView_QuoteId_ViewedAt)` | 3,800 | 1 |
| Q4 | `Index Seek(IX_QuoteView_CountryCode)` → `Nested Loops` → `Clustered Index Seek` | 26 | **26** |
| Q5 | `Clustered Index Scan(CIX_QuoteView_ViewedAt)` | 55,417 | 1 |

**No `Sort` appears in any of the five plans.** That is the `DESC` in the key of
non-clustered #1 doing its job, and the one thing in this table I would check
first after any DDL change.

`Executes = 26` on Q4's lookup is the whole clustered-versus-non-clustered
argument in one cell: one round trip to the clustered index per matched row. Q5's
plan is the same shape's refusal, and section 3 of that file confirms it by
differencing `sys.dm_db_index_operational_stats` across the five statements:

```
IndexName|IndexType|RangeScans|SingletonLookups
---------|---------|----------|----------------
CIX_QuoteView_ViewedAt|CLUSTERED|2|26
IX_QuoteView_QuoteId_ViewedAt|NONCLUSTERED|4|0
IX_QuoteView_CountryCode|NONCLUSTERED|1|0
```

26 singleton lookups on the clustered index — exactly Q4's 26 matched rows, and
not one more. Zero from the covering index across four range scans (Q2's one plus
Q3's three). The counters and the plans agree, which is the point of having both.

---

## The write-side cost

**One line, as asked:** the same 20,000-row insert cost **2.2× the transaction
log and 4.1× the logical reads** once the two non-clustered indexes existed,
because 20,000 rows became 62,000 index entries — and updating a column that *is*
an index key cost **3.8×** the log of the same update on an unindexed heap,
against 1.27× for a column no index mentions.

Four tables with byte-identical DDL — the last three created by
`SELECT TOP (0) * INTO` from the first, so they cannot drift apart — loaded with
the same 20,000 rows in the same twenty batches of a thousand.

```
Scenario|Structure|RowsTouched|LogicalReads|LogKB|LogBytesPerRow|VsHeap
--------|---------|-----------|------------|-----|--------------|------
insert 20,000 rows|heap|20000|46801|5315.6|272.2|1.00
insert 20,000 rows|clustered only|20000|38297|5968.7|305.6|1.12
insert 20,000 rows|clustered + 2 NC|20000|191605|11708.3|599.5|2.20
insert 20,000 rows|clustered on a GUID|20000|93892|8989.4|460.3|1.69
update column in no index|heap|2000|4912|203.2|104.1|1.00
update column in no index|clustered + 2 NC|2000|2554|257.9|132.1|1.27
update an index key column|heap|2000|4446|203.2|104.1|1.00
update an index key column|clustered + 2 NC|2000|24864|776.6|397.6|3.82
```

Log bytes read from `sys.dm_tran_database_transactions` inside the transaction
before it commits — every change to every index is logged, so it is close to a
direct count of the work index maintenance created. Not elapsed time, for the
same reason as above. **Why batched:** given the whole set in one
`INSERT … SELECT`, the optimiser sorts into clustering-key order and fills pages
from the left, so even a random key produces no splits and `WriteRandomKey` looks
free. Twenty batches of a thousand is how an application writes.

**A clustered index is nearly free on the write side; non-clustered indexes are
not.** 1.12× for the clustered index alone, 2.20× once the two non-clustered
indexes exist. And the *read* side of a write tells the same story louder —
191,605 logical reads against 38,297, because each insert has to navigate three
trees instead of one.

**Write amplification, counted rather than argued about.** `leaf_insert_count`
per index on `perf.WriteIndexed`: 20,000 in the clustered index, 20,000 in
`IX_..._QuoteId_ViewedAt`, and 22,000 in `IX_..._CountryCode` — the extra 2,000
being the `CountryCode` update, which is a delete and an insert because the row
must move to keep the index in order. 62,000 index rows for 20,000 table rows.

**Updating an indexed key is the expensive one, and it does not look expensive.**
The two statements in section 5 differ only in which column they set. `UserId` is
in no index: 1.27×. `CountryCode` is non-clustered #2's key: 3.82× the log and
5.6× the logical reads. Both filter on `ViewId`, which leads no index on either
table, so both plans scan to find their rows and the comparison is fair.

**Giving up a sequential clustering key costs more than the extra indexes do.**

```
TableName|IndexName|IndexType|page_count|AvgPageFullnessPct|FragmentationPct
---------|---------|---------|----------|------------------|----------------
perf.WriteClustered|CIX_WriteClustered|CLUSTERED INDEX|422|98.80|4.74
perf.WriteHeap|(heap)|HEAP|422|98.80|1.89
perf.WriteIndexed|CIX_WriteIndexed|CLUSTERED INDEX|422|98.80|4.74
perf.WriteIndexed|IX_WriteIndexed_QuoteId_ViewedAt|NONCLUSTERED INDEX|143|50.09|97.90
perf.WriteIndexed|IX_WriteIndexed_CountryCode|NONCLUSTERED INDEX|74|84.46|77.03
perf.WriteRandomKey|CIX_WriteRandomKey|CLUSTERED INDEX|622|67.03|99.36
```

Identical rows, identical width, identical batches. The sequential key holds
98.80% page fullness at 4.74% fragmentation. The GUID key is 99.36% fragmented at
67.03% full, so it needs **622 pages instead of 422** — 47% more — to hold the
same data, and it stays that way until someone rebuilds it. Fragmentation slows a
scan down; poor page fullness makes the scan *bigger*, permanently. That is the
cost worth caring about.

**And the non-clustered indexes fragment worse than the GUID does.** I predicted
`IX_..._CountryCode` would show the same effect "in miniature" and it isn't
miniature: `IX_..._QuoteId_ViewedAt` sits at **50.09% page fullness and 97.90%
fragmentation** — half-empty pages, because `QuoteId` has no relationship to
insert order and every batch splits pages across all 80 values at once. It is the
worst-fragmented structure in the experiment, and it is one of the two indexes
this piece recommends.

Total space, all structures: 647 pages for `WriteIndexed` against 423 for the
heap — **53% more** for the same 20,000 rows.

---

## What I would keep

Selectivity is a property of the value, not the column. The same index, the same
column, and the same query shape is a 13.9× improvement for `'MT'` and an 88×
regression for `'IN'`, and nothing in the DDL, the query text, or "is the index
being used?" distinguishes them.

The corollary is about test data. A uniform 100k-row fixture would have made
`IX_QuoteView_CountryCode` look unambiguously good, because every value would
have been about a seventh of the table and the tipping point would never have
been crossed. The skew in the generator is what made the lesson visible, and skew
is what production data has.

The corollary I did not expect is about fragmentation. Everyone knows to avoid a
GUID clustering key. Almost nobody checks page fullness on the *non-clustered*
indexes, and here the recommended covering index is half-empty for exactly the
same reason.

---

## What would break this

**One measurement did not work.** `leaf_allocation_count` was meant to be the
page-split proxy, and it is not usable: it reads 1 for every clustered index in
the experiment — including the GUID-keyed one that is 99% fragmented — while
reading 422 for the heap and 74 for one non-clustered index. Whatever it counts,
it is not comparable across structure types, and the fragmentation figures in 6e
are what carry that argument instead. Left in the output rather than quietly
dropped, because a reader looking for the split count should see why it isn't
there.

**The table is 15 MB, so it fits entirely in the buffer pool.** Every number here
is a warm-cache measurement — `physical reads 0` throughout. Logical reads are
the right metric precisely because they do not care, but the *decisions* the
optimiser makes change when a table no longer fits in memory, and 169,885 logical
reads that are all buffer hits is a very different afternoon from 169,885 that
are not.

**The summary grid is not the authoritative number.** Section 6a counts per
request, including `OPTION (RECOMPILE)`'s catalog reads, so it overstates every
query by 20–40 pages and overstates a three-page covering seek by 8×. The
per-table `STATISTICS IO` output is the record; if the two disagree, believe
`STATISTICS IO`.

**One column of `12_write_cost.txt` is not reproducible.** Everything on the read
side has come back byte-identical across four rebuilds, and so have the log-byte,
page-count and ratio figures. `LogicalReads` in section 6a does not: it moves by
up to about 1.4% between runs, because the pages an insert touches to navigate
and extend an index depend on allocation and read-ahead behaviour that is not
fully determined by the row set. The log bytes are the measure to quote for write
cost; treat the read column there as indicative.

**`MAXDOP 1` is a measurement decision, not a recommendation.** Real queries on
this shape of table would go parallel, and the plan chosen at DOP 8 is not always
the serial plan with more threads.

**Fragmentation depends on insert order, and here that is twenty batches of a
thousand.** One batch of 20,000 produces no splits at all; 20,000 single-row
inserts produce more than this does. The comparison between the two clustering
keys holds at any batch size; the absolute percentages do not.

**`perf.QuoteView` has no primary key, no foreign key and no check constraints.**
Deliberate — a foreign key is enforced by a read against another index on every
insert, and it would be counted here as index maintenance. A real version of this
table would declare `PRIMARY KEY NONCLUSTERED (ViewId)`, which is a fourth
structure and a fourth write on every insert.

**The data is synthetic and the referrers are `example.com` and `example.org`,**
which RFC 2606 reserves for exactly this. No real user agent, address, or person
is represented in any row.
