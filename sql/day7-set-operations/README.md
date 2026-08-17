# Day 7 — set operations from a spec

## The exercise

Three business questions, answered with `UNION` / `INTERSECT` / `EXCEPT` where
appropriate, noting which operator was used for each and why.

Database: `QuotesLab` on SQL Server 2022 — the same one built for
[joins and CTEs](../day7-joins-and-ctes/README.md) and
[window functions](../day7-window-functions/README.md).
All queries are in [`08_set_operations.sql`](08_set_operations.sql); full output
is in [`results/08_set_operations.txt`](results/08_set_operations.txt).

## The part that isn't SQL

The SQL here is the easy half. Two of the three questions have more than one
meaning, and the third names sets that do not exist in this schema at all. So
the work is translation, and the rule I held to was: where a question is
ambiguous, answer both readings rather than quietly picking one; where the
vocabulary has no counterpart in the schema, write the mapping down in the query
as a named CTE, so a reviewer can disagree with the translation instead of
reverse-engineering it.

One constraint I set deliberately: **nothing here modifies the seed.** Pieces 1
and 2 are already submitted against that data, and reshaping a shared fixture so
a query returns prettier rows would invalidate their captured evidence. Where
the honest answer is the empty set, the empty set is what this returns.

---

## Q1 — "authors with quotes but no tags"

**Operator: `EXCEPT`.** The question is literally a subtraction — start from the
authors who have written, remove the ones that have tags. Expressed as a join or
a `NOT EXISTS` it is the same answer, but `EXCEPT` is the only form whose shape
matches the shape of the sentence, and this piece is about translation.

The sentence parses two ways, and they disagree:

**A. Authors who have quotes and have no tags at all** — subtracting sets of
authors. The result is empty:

```
AuthorId|FullName
--------|--------

AuthorsWithQuotes|AuthorsWithTaggedQuotes
-----------------|-----------------------
16|16
```

An empty result is indistinguishable at a glance from a query that is silently
broken, so the two operands' cardinalities are printed beside it. They are
equal, which is why the difference is empty: in this dataset every author who
has written has tagged at least one quote. That is a finding, not a failure.

**B. Authors who own at least one untagged quote** — the same operator one grain
lower, subtracting sets of `(author, quote)` pairs and then asking who is left:

```
FullName|UntaggedQuoteCount|UntaggedQuoteIds
--------|------------------|----------------
Alan Turing|1|30
Donald Knuth|2|53, 55
Mark Twain|1|18
```

Three authors, and every one of them has tagged other quotes — which is exactly
why reading A returned nothing. If someone asks this question while auditing tag
coverage, B is almost certainly what they meant, and answering A would have sent
them away believing coverage was complete.

---

## Q2 — "authors in both the 'classic' and 'modern' sets"

**Operator: `INTERSECT`.** "In both" is membership of two sets at once, and that
is the operator whose definition is that sentence.

`QuotesLab` has no 'classic' or 'modern' marker — no column, no tag, no
collection. So the sets have to be defined, and the definition is a judgement
call that belongs in the open. The mapping used reads the two canons off the
category tree:

- **classic** — authors with a live quote under the **Philosophy** or
  **Literature** roots (ethics, logic, stoicism, fiction, poetry)
- **modern** — authors with a live quote under the **Science** root (physics,
  mathematics, computer science and everything below it)

A recursive `RootOf` CTE attributes every category to its root, so a quote filed
under Concurrency counts toward Science three levels up. That is the same
traversal as piece 1's section 5, reused rather than restated.

```
AuthorId|FullName
--------|--------
8|Alan Turing
14|C. A. R. Hoare
11|Donald Knuth
10|Edsger W. Dijkstra
13|Leslie Lamport

ClassicOnly|ModernOnly|Both
-----------|----------|----
5|6|5
```

Five authors write in both canons. The three-way split uses `EXCEPT` twice and
`INTERSECT` once over the same two sets, which is the cheapest way to show the
partition is coherent — 5 + 5 = 10 classic, 6 + 5 = 11 modern.

**The rival reading, which is worth running.** If 'classic' and 'modern'
describe the *author* rather than the subject matter, the obvious column is
`BirthYear`:

```
ClassicAuthors|ModernAuthors|UnknownEra|InBothEras
--------------|-------------|----------|----------
8|8|3|0
```

Zero, by construction — nobody is born in two centuries. The operator is still
correct; the *definition* makes the question unanswerable. This is the failure
mode worth naming: a business question that presumes overlap, translated onto a
rule that forbids it, returns an empty set that looks exactly like a data
problem. The fix is not SQL. It is going back and asking what 'classic' means.

---

## Q3 — "the combined distinct tag list across two categories"

**Operator: `UNION`, not `UNION ALL`.** The word doing the work is *distinct*: a
tag used in both categories must appear once. `UNION` deduplicates, `UNION ALL`
concatenates.

Categories chosen: Algorithms and Software Engineering, two siblings under
Computer Science with genuinely different tag profiles.

```
TagId|TagName
-----|-------
7|design
2|engineering
4|failure
6|leadership
3|simplicity
8|testing

ViaUnion|ViaUnionAll
--------|-----------
6|25
```

Six distinct tags, from twenty-five tag applications. The row counts are the
whole argument: `UNION ALL` would have returned a 25-row "distinct tag list", a
result whose name is a lie. `UNION ALL` is still the right default anywhere
duplicates are impossible, because deduplication is not free — just not here,
where removing it is the entire requirement.

---

## Three behaviours of set operators that bite

**Set operators treat NULL as equal to NULL. Joins do not.** Three authors have
a NULL `BirthYear`. `EXCEPT`-ing that set from itself returns nothing, so
`EXCEPT` matched the NULLs. The same intent as an anti-join returns all three
rows, because `b.BirthYear = a.BirthYear` is UNKNOWN whenever the value is NULL:

```
-- EXCEPT: 0 rows          -- anti-join: 3 rows
                            Hypatia|NULL
                            Seneca|NULL
                            Sun Tzu|NULL
```

Same data, same question, opposite answer. This matters here because
`Quote.CategoryId` and `Author.BirthYear` are both nullable, so it is not a
theoretical concern.

**`INTERSECT` binds tighter than `UNION` and `EXCEPT`.** Without parentheses,
`{1,2} UNION {2,3} INTERSECT {3,4}` evaluates the `INTERSECT` first and returns
`{1,2,3}`. Forcing left-to-right with parentheses returns `{3}`. Two very
different answers from the same three sets and no syntax error either way.

**Column names come from the first query, and `ORDER BY` belongs to the whole
set expression.** The second `SELECT` can name its column anything; the result is
named by the first. `ORDER BY` may appear only at the very end and must use the
first query's names.

## `EXCEPT` vs `NOT EXISTS` vs anti-join

All three express Q1-B and all three agree here — 4 rows each, 0 disagreement,
checked with `EXCEPT` in both directions:

```
ExceptRows|NotExistsRows|AntiJoinRows|DisagreementRows
----------|-------------|------------|----------------
4|4|4|0
```

They are interchangeable *because the projected columns are already unique*, and
that is the condition worth remembering. `EXCEPT` applies DISTINCT to its result
whether or not you asked for it, so on a set that legitimately contains
duplicates it silently collapses them, while `NOT EXISTS` leaves the outer row
count alone. Prefer `EXCEPT` when the sentence is a subtraction and the dedup is
harmless or wanted; prefer `NOT EXISTS` when the outer side must keep its
cardinality, or when the two sides do not share a column list.

## What would break this

The translation, not the SQL. Every query here is correct against the mapping
directly above it and meaningless if the mapping is wrong, and nothing in the
database can tell you which. Q2 is the clear case: two defensible readings of
'classic' and 'modern' give 5 authors and 0 authors respectively, and both are
returned by a correct `INTERSECT`.

`EXCEPT`'s implicit DISTINCT is the silent one. Add a column that varies within
an author — a quote id, a timestamp — and rows that previously collapsed to one
stop collapsing, changing the count with no error and no diff in the operator.

The Q2 canon mapping ignores quotes with a NULL `CategoryId`, since a quote with
no category belongs to no root. That is five live quotes excluded from both
canons by design, and it means "classic + modern + neither" does not sum to the
79 live quotes.

Finally, `UNION` deduplicates on *every* projected column. Q3 projects
`(TagId, TagName)`, which is safe because they are functionally dependent. Add
the category name to that projection and each tag reappears once per category —
still a correct `UNION`, no longer a distinct tag list.
