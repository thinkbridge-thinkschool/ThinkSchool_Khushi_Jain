# Day 9 — Deadlock

Two sessions each hold one row and then ask for the other's. Shown deadlocking,
then fixed by taking the rows in the same order.

- [`session_a.sql`](session_a.sql) — row 1, then row 2
- [`session_b.sql`](session_b.sql) — row 2, then row 1, then the same fix
- [`results/`](results) — output of both sessions from the last run

Both run against `txn.QuoteStat` and the signal harness that
[day9-isolation-levels](../day9-isolation-levels/README.md) creates, so that
piece runs first.

## What happened

| | Session A | Session B |
|---|---|---|
| 1 | `UPDATE` row 1 — holds it | `UPDATE` row 2 — holds it |
| 2 | asks for row 2 — waits on B | asks for row 1 — waits on A |

Neither can finish, so SQL Server kills one. `SET DEADLOCK_PRIORITY LOW` in
session B makes it the victim every run, instead of whichever transaction had
done less work.

## How to reproduce

Start `session_a.sql` first, then `session_b.sql` within a couple of seconds —
`../run-lab.ps1` does this. They then wait for each other through the signal
table, so the cycle does not depend on the timing of the start.

## Evidence

The victim message, from `results/session_b.txt`:

```
1205|13|Transaction (Process ID 56) was deadlocked on lock resources with
another process and has been chosen as the deadlock victim. Rerun the transaction.
```

The full deadlock graph is in `results/session_a.txt`, read from the always-on
`system_health` Extended Events session — no trace flag or new session needed.
Its `<resource-list>` is the cycle:

```xml
<keylock objectname="QuotesLab.txn.QuoteStat" indexname="PK_QuoteStat" mode="X">
  <owner-list><owner  id="processe0041b088" mode="X"/></owner-list>
  <waiter-list><waiter id="processe0042a8c8" mode="X"/></waiter-list>
</keylock>
<keylock objectname="QuotesLab.txn.QuoteStat" indexname="PK_QuoteStat" mode="X">
  <owner-list><owner  id="processe0042a8c8" mode="X"/></owner-list>
  <waiter-list><waiter id="processe0041b088" mode="X"/></waiter-list>
</keylock>
```

Each process owns one key exclusively and waits for the other. `processe0042a8c8`
is spid 56 at priority -5, which is session B, and it is the one named in
`<victim-list>`.

Three sections are removed before printing: `stackFrames`, `executionStack` and
`inputbuf`. The first is a symbol dump of this SQL Server build, the other two
only quote the two scripts in this folder, and together they are most of the
graph. Each `PRINT` is also sliced to 800 characters, because the driver
truncates a single message at about 1 KB.

## Fix

Both transactions now take row 1 before row 2. One of them gets row 1 first and
the other queues behind it, so there is no cycle to break — a circular wait needs
each session to hold something the other wants next, and a shared order makes
that impossible.

## Verification

Session B's phase 2 commits with no error, and while it waits, session A's
`ShowBlockedSessions` records it on `LCK_M_X` — it is genuinely contending for
row 1, not just running later.

Both rows advance by three per run, not four: session A's two transactions and
session B's fixed one, with session B's deadlocked transaction rolled back.
