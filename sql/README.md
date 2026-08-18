# QuotesLab — the SQL workbench

A SQL Server database holding the Week-1 Quotes domain, built for the SQL
exercises rather than for the API. It stands alongside `QuotesApi/` and shares
nothing with it: no EF Core model points here, no migration touches it, and
dropping it costs nothing.

## Why a separate database

The shipped `QuotesApi` schema cannot answer the Day-7 question. `Quote.Author`
is an `nvarchar` column rather than a foreign key, so an author who has written
nothing has no row to return and "each author" has no meaning. There is no
timestamp on `Quote` at all, so "their most-recent quote" has no answer that is
not an accident of identity ordering. Nothing in the model is hierarchical, so a
recursive CTE has nowhere to recurse.

Reshaping the running API's schema to suit a SQL exercise would be the wrong
trade — a migration, two migration sets to keep in step, and a domain model
changed for a reason the domain does not have. QuotesLab instead reproduces the
same concepts with the three additions the exercises need, which is what the
task means by "a fresh SQL Server".

## Why SQL Server

SQLite backs the API locally and is the wrong tool from here on. It has no
execution plans worth reading, no `SET STATISTICS IO`, no dynamic management
views, no `OPTION (MAXRECURSION)`, no filtered indexes, and no stored
procedures. Every one of those is the subject of a later exercise, so choosing
SQLite now would mean choosing again in a week.

SQL Server 2022 is also already the repository's answer to "a real database":
`QuotesApi.Tests/SqlServer/` runs the integration suite against this exact image
through Testcontainers, and the image is therefore already pulled on any machine
that has run the tests. Introducing PostgreSQL would mean a third engine, a
second dialect, and no benefit the exercises can use.

Everything in `schema/`, `day7-*/` and `day8-indexes/` is Azure SQL compatible
apart from the `CREATE DATABASE` batch, so the same scripts run unchanged against
an Azure SQL database when one is provisioned.

## Running it

Docker is the only prerequisite. SQL Server rejects weak passwords, so generate
one into the shell session rather than inventing one, and do not put it in a
file:

```powershell
$bytes = New-Object byte[] 24
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$env:MSSQL_SA_PASSWORD = [Convert]::ToBase64String($bytes) + '!aA1'

./sql/run-lab.ps1
```

This is a PowerShell script. From Git Bash or any other shell, invoke it through
PowerShell rather than directly:

```bash
powershell -ExecutionPolicy Bypass -File sql/run-lab.ps1
```

The script starts the container, waits for its healthcheck rather than sleeping
a guessed interval, applies the schema and seed, runs the exercise scripts in
order, and writes every result set to the `results/` folder beside the script
that produced it. `-SchemaOnly` rebuilds the database without running the
queries. `-Stop` removes the container and its data with it.

Day 8 generates its own 100,000-row table and adds a minute or two to the run.

Nothing persists between runs, on purpose. SQL Server reads
`MSSQL_SA_PASSWORD` only when it initialises a fresh data directory and ignores
it on every later start, keeping whatever password is already baked into
`master` — so a persisted data directory plus a per-session password is a
guaranteed login failure the second time. Since the schema script rebuilds the
database from scratch anyway, there is nothing to persist.

The password reaches sqlcmd through `SQLCMDPASSWORD`, read from the container's
own environment. It is never a command-line argument, so it appears in no
process list and in none of the captured output.

## Two schemas

`app` holds the domain: authors, quotes, categories, tags, collections. Sixteen
authors and eighty quotes, which is the right size for reading a join by eye and
the wrong size for measuring anything.

`perf` holds tables that exist only to be measured — Day 8's 100,000-row view
log and the write-cost clones beside it. Nothing in `app` references anything in
`perf`, so the Day-8 tables can be dropped or regenerated without touching the
data the Day-7 answers were captured against. That separation is the point:
adding 100,000 rows to `app.Quote` would silently invalidate three already
submitted pieces.

## Reproducibility

`01_schema.sql` drops and recreates the database, so identity values start from
1 every time. `02_seed.sql` uses fixed `datetime2` literals and never calls
`GETDATE()`. Day 8's 100,000 rows are derived arithmetically from row numbers
through `HASHBYTES`, with no `RAND()` and no `NEWID()`, so the row set is
identical on every machine and page counts are comparable between runs.

Two runs a month apart produce byte-identical result files, which is what makes
the committed output in `results/` evidence rather than a snapshot. The one
figure deliberately left out of every captured file is elapsed time, which is
not reproducible and not needed: logical reads count the same work without
depending on cache state or on what else the machine is doing.

## Connecting by hand

The container publishes port 11433, chosen so an existing local SQL Server or
LocalDB instance is left alone.

| Setting | Value |
|---|---|
| Server | `localhost,11433` |
| Authentication | SQL login |
| User | `sa` |
| Password | whatever `MSSQL_SA_PASSWORD` was set to |
| Database | `QuotesLab` |
| Encryption | trust the server certificate |

## Layout

| Path | What it is |
|---|---|
| `schema/01_schema.sql` | Tables, constraints, indexes. Drops and recreates the database |
| `schema/02_seed.sql` | Deterministic seed data, with the edge cases the exercises hunt for |
| `day7-joins-and-ctes/` | Day 7, piece 1 — the join and CTE exercises, and the submitted answer |
| `day7-window-functions/` | Day 7, piece 2 — ranking, `LAG`/`LEAD`, running totals, and window frames |
| `day7-set-operations/` | Day 7, piece 3 — `UNION`/`INTERSECT`/`EXCEPT`, and translating a vague spec |
| `day8-indexes/` | Day 8, piece 1 — clustered vs non-clustered, measured over 100,000 rows |
| `*/results/` | Captured output from the last run. Committed, because it is the exercises' evidence |
| `docker-compose.yml` | The SQL Server container |
| `run-lab.ps1` | Start, apply, run, capture |

## A note on the seed data

The author names are real historical figures and their biographical columns are
ordinary public facts. The quote texts are not real quotations — they are
synthetic sentences written for the exercise. Attributing invented words to a
named real person is a misattribution whatever the intent, and this database
exists to exercise SQL rather than to be a quotation reference. The user rows
are placeholders on `example.com`, which RFC 2606 reserves for exactly this.
