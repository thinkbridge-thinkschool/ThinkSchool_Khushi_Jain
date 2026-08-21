# Day 7 — set operations

Three business questions answered with `UNION`, `INTERSECT` and `EXCEPT`, saying
which operator each one needs and why. The queries are in
[`08_set_operations.sql`](08_set_operations.sql), the output in
[`results/`](results).

Database: `QuotesLab` on SQL Server 2022, the same one used for
[joins and CTEs](../day7-joins-and-ctes/README.md) and
[window functions](../day7-window-functions/README.md).

## Q1 — authors with quotes but no tags

**`EXCEPT`**, because the question is a subtraction: authors who have written,
minus the ones that have tags.

It reads two ways. Authors with no tags *at all* is empty — every author who has
written has tagged something. Authors who own at least one *untagged quote* is
the same operator over `(author, quote)` pairs, and returns three:

```
FullName|UntaggedQuoteCount|UntaggedQuoteIds
--------|------------------|----------------
Alan Turing|1|30
Donald Knuth|2|53, 55
Mark Twain|1|18
```

Both are answered, since the first would tell anyone auditing tag coverage that
coverage was complete.

## Q2 — authors in both the 'classic' and 'modern' sets

**`INTERSECT`**, because "in both" is membership of two sets at once.

The schema has no classic or modern marker, so the query defines them off the
category tree: classic is a live quote under Philosophy or Literature, modern is
one under Science. A recursive `RootOf` CTE maps each category to its root, so a
quote under Concurrency counts toward Science.

```
AuthorId|FullName        ClassicOnly|ModernOnly|Both
--------|--------        -----------|----------|----
8|Alan Turing            5|6|5
14|C. A. R. Hoare
11|Donald Knuth
10|Edsger W. Dijkstra
13|Leslie Lamport
```

Read the other way — classic and modern describing the author's era, off
`BirthYear` — the answer is 0, since nobody is born in two centuries. Same
correct `INTERSECT`, and the definition decides the answer.

## Q3 — the combined distinct tag list across two categories

**`UNION`, not `UNION ALL`**, because *distinct* means a tag used in both
categories appears once. Over Algorithms and Software Engineering: 6 distinct
tags from 25 tag applications. `UNION ALL` would have returned a 25-row
"distinct tag list".

## Three behaviours worth knowing

Set operators treat NULL as equal to NULL; joins do not. `EXCEPT`-ing the three
NULL-`BirthYear` authors from themselves returns 0 rows, while the same intent as
an anti-join returns all 3.

`INTERSECT` binds tighter than `UNION` and `EXCEPT`. Unparenthesised,
`{1,2} UNION {2,3} INTERSECT {3,4}` returns `{1,2,3}`; forced left to right,
`{3}`. No syntax error either way.

`EXCEPT` applies DISTINCT whether or not you asked, so it silently collapses
genuine duplicates where `NOT EXISTS` would keep them. Here all three forms give
4 rows, because the projected columns are already unique.
