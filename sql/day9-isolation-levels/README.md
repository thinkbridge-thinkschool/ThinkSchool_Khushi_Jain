# Day 9 — isolation levels and the read anomalies

Two sessions run at the same time: [`session_a.sql`](session_a.sql) reads,
[`session_b.sql`](session_b.sql) writes. Each anomaly is shown happening, then
shown being stopped by raising the isolation level one step. Output from the last
run is in [`results/`](results).

## Anomaly → lowest isolation level that prevents it

| Anomaly | Lowest level that prevents it |
|---|---|
| Dirty read | `READ COMMITTED` |
| Non-repeatable read | `REPEATABLE READ` |
| Phantom read | `SERIALIZABLE` |

Each level also prevents everything the levels below it prevent.

## The six steps

| Step | Session A (reader) | Session B (writer) | Result |
|---|---|---|---|
| 1 | `READ UNCOMMITTED`, reads row 1 | Sets row 1 to 111, rolls back | A sees **111**, then 100. Dirty read |
| 2 | `READ COMMITTED`, reads row 1 | Sets row 1 to 112, rolls back | A waits, reads **100**. Prevented |
| 3 | `READ COMMITTED`, reads row 2 twice in a transaction | Sets row 2 to 222 between the reads | A sees **200 then 222**. Non-repeatable read |
| 4 | `REPEATABLE READ`, reads row 2 twice in a transaction | Tries to set row 2 to 999 | B waits, A sees **222 twice**. Prevented |
| 5 | `REPEATABLE READ`, counts rows 10–19 twice in a transaction | Inserts row 11 between the counts | A sees **1 then 2**. Phantom |
| 6 | `SERIALIZABLE`, counts rows 10–19 twice in a transaction | Tries to insert row 12 | B waits, A sees **2 twice**. Prevented |

`REPEATABLE READ` locks the rows it read, but a row that does not exist yet
cannot be locked — so step 5's insert slips in. `SERIALIZABLE` locks the range
including the gaps, so step 6's does not.

In the prevented steps the writer is blocked, not ignored; its write goes through
as soon as the reader commits. `ShowBlockedSessions` in the output shows it
waiting.

## Notes

`txn.QuoteStat (QuoteId, LikeCount)` starts with rows 1, 2 and 10. `QuoteId` is
the clustered primary key, so `SERIALIZABLE` has an index to take range locks on.
Its own `txn` schema keeps the Day-7 and Day-8 data untouched.

The two sessions take turns through `txn.Signal`: one inserts a name, the other
polls for it. The poll uses `NOLOCK`, because signals are raised inside open
transactions. A blocked session cannot signal, so in the prevented steps it
signals just before the statement that blocks.

Run it with the rest of the lab:

```bash
powershell -ExecutionPolicy Bypass -File sql/run-lab.ps1
```
