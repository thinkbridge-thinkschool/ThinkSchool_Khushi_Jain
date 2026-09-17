# Day 31 — Polish: tests, perf, security

Pull request: https://github.com/thinkbridge-thinkschool/ThinkSchool_Khushi_Jain/pull/2

[DocBook](../Capstone%20Project/DESIGN.md) had six tests and no CI covering any of it. `ci.yml` built
`ThinkSchool.slnx`, which lists no DocBook project, so the green check on yesterday's PR was about
unrelated code. That was the first thing to fix and everything else followed from it.

## The CI gate

A second job in [`ci.yml`](../.github/workflows/ci.yml) restores, builds and tests
`Capstone Project/DocBook.slnx` with coverage, separate from the existing job so the two coverage
gates stay readable. It runs on `ubuntu-latest`, which has a Docker daemon — which the integration
tests need, because they start a real SQL Server.

120 tests, up from 6.

## Coverage at each layer

| Layer | Assembly | Line |
| --- | --- | --- |
| Domain | `Scheduling.Domain` | 93.9% |
| | `Patients.Domain` | 88.9% |
| Application | `Scheduling.Application` | 100% |
| | `Patients.Application` | 100% |
| | `Notifications.Application` | 100% |
| Host and adapters | `DocBook.Api` | 96.2% |
| | `DocBook.Infrastructure` | 97.4% |
| | `Scheduling.Infrastructure` | 95.3% |
| | `Patients.Infrastructure` | 94.5% |
| | `Notifications.Infrastructure` | 64.7% |

Each figure comes from the layer's own tests, not from everything that happened to load. The domain
projects have no database; the application tests run the handlers against fakes; the API tests host
the real pipeline over a SQL Server container.

`Notifications.Infrastructure` is the one weak number and the reason is honest: `EmailNotificationSender`
never runs in a test. Email is off unless configured, and the end-to-end test swaps in a recorder so it
can see what was sent. The only proof that class works is the real confirmation email a booking against
the deployed database produced yesterday.

The gate is set at 60% line coverage rather than 70%. Coverlet checks the threshold per test project,
and the narrowest project sets the floor: a run of the Patients domain tests instruments
`DocBook.SharedKernel` too, whose base classes only the layers above exercise in full, so that run
measures 65% however good the domain tests are.

## Two bugs the tests found

**Cancelling an appointment returned 500 for everyone, always.** `FindByAppointmentAsync` asked EF for
the shadow foreign key as a `Guid`, but it is a `DoctorDayScheduleId` behind a value converter and the
shaper has no cast between them. This is the same mistake, in the same shape, that I fixed on the read
side on Day 29 — I fixed the one the happy path walked through and left its twin alone, because
nothing cancels an appointment in that script. It now finds the schedule through the navigation, which
is one query instead of two.

**A test was passing for the wrong reason.** `Book_rejects_a_slot_outside_the_opening_hours` used a
slot starting at 08:00 against a clock reading 08:00, so it tripped the "cannot start in the past"
guard and never reached the opening-hours check. Asserting on `DomainException.Code` rather than the
exception type is what exposed it. The codes are the contract — the messages never reach a caller —
so a test that only asserts the type is not testing anything the API promises.

## The hot path

`GET /doctors/{id}/days/{date}/free-slots`. Every booking is preceded by several slot listings, and it
is the only route a patient polls. [`perf/read-paths.js`](../Capstone%20Project/perf/read-paths.js)
drives 20 users at it for 45 seconds against a day that is twelve-sixteenths booked, then does the same
to `GET /appointments/mine`. It fails the run if more than 1% of responses are not 200, so a throttled
run cannot be read as a fast one.

| | before p99 | after p99 | | before max | after max |
| --- | --- | --- | --- | --- | --- |
| free slots | 18.28ms | **15.34ms** | −16% | 348.51ms | 114.69ms |
| my appointments | 29.83ms | **15.80ms** | −47% | 372.14ms | 90.54ms |

Throughput went from 1,960 to 2,360 requests a second.

The change is one line: the free-slot read goes through a no-tracking query now. Every request was
building change-tracker snapshots for a schedule and up to sixteen appointments and then discarding
them. The write path still tracks, because it has to.

The result I did not expect is the second row. I changed nothing on that path — same query, same index,
same handler — and its p99 nearly halved. The tail here is not SQL, it is garbage collection, and the
allocations free slots stopped making paid for pauses the whole process was feeling. The maximums
collapsing on both rows say the same thing.

Measured on a release build against a local SQL Server, with the rate limits raised: twenty users share
one token, and the real limit of 120 a minute per caller would have made this a measurement of the
limiter. One run before and one after, so the direction is trustworthy and the exact percentages are
not.

## The security re-check

Two advisories, both real, both now pinned past. `Microsoft.OpenApi` 2.0.0 carries
[GHSA-v5pm-xwqc-g5wc](https://github.com/advisories/GHSA-v5pm-xwqc-g5wc), a stack overflow when
*parsing* a document with circular references; DocBook only ever writes one, so the exposure is
nil — but a high-severity warning on every build that nobody can act on is one everybody learns to
scroll past. `SSH.NET` 2025.1.0 carries
[GHSA-q939-rpr3-3284](https://github.com/advisories/GHSA-q939-rpr3-3284) and reaches the test project
through Testcontainers.

**Every deliberate refusal was logging a stack trace at error level.** `ExceptionHandlerMiddleware`
logs the exception before our handler turns it into a clean 409, so a slot that is simply taken read
as a failure. Our own handler already records every path — information for a mapped refusal, error
with the exception for a real one — so that middleware's logging is off now. The cost is that a
failure inside the handler itself would go unlogged.

**A registration without a phone number was an unhandled path.** The field is optional by design and
the domain never null-checks it. It is nullable at the edge now, and the test that holds it there
registers and then signs in, because registration answers 202 either way and the status alone proves
nothing.

The rest of the re-check found nothing to fix. The confirmation email is plain text, so the
cancellation reason a patient types cannot carry markup into their own inbox. The send log records a
subject, a patient id and an operation id — not the address, not the body. `handled_messages` holds
an appointment id, a kind and a timestamp, and names nobody. The Communication Services connection
string is in no file in this repository, and `ValidateOnStart` refuses a boot that enables email
without one.

I did not re-run the ZAP baseline. Day 27's scan reached three URLs and all three were 404s, because
every route but `/health` needs a token, so a second run says the same thing. The header rules it
checked are asserted now by a test that runs on a 200 and on a 401 on every build, which is worth
more than a scan somebody remembers to run.

## What I learned this session

A test that asserts an exception type is not testing the promise the API makes; the promise is the
code, and asserting the type let a test pass for four days while checking a rule it was not written
for. The other half of the same lesson is that fixing a bug is not the same as fixing its class — the
`EF.Property<Guid>` mistake had a twin two methods away and I shipped it twice.

## What would break this

The p99 numbers are one run each against a local container on my laptop, so they describe this machine
and not the deployed API. Nothing in CI measures performance, which means the no-tracking read could be
reverted tomorrow and every test would still be green.
