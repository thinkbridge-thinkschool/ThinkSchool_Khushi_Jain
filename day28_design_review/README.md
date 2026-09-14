# Day 28 — Design review and ADR

A critique of [DocBook](../Capstone%20Project/DESIGN.md), the capstone, the ADR for the decision the
rest of the design rests on, and the plan for the four build days that follow.

The mentor pass asked whether the architecture holds and whether the design document tells the truth
about the code. The peer pass asked whether I could work in this code and what breaks when it runs
unattended. Both read the code rather than the design document, which is the only way the first
question has an answer.

## The mentor critique

**The design promises idempotent consumers, and there are none.** `DESIGN.md` says delivery is
at-least-once and that this is safe because "Notifications keys on the appointment id and the message
kind". No such key exists anywhere in the module — no table, no lookup, no check.
`AppointmentNotifications.cs` finds the patient and sends, every time.

**Nothing claims an outbox row.** `TakePendingAsync` is a plain `WHERE ProcessedAt IS NULL` with no
lease, so a second instance reads the same rows and delivers them again. Redelivery is not the rare
crash case the design treats it as; it is what happens every ten seconds on a scaled-out deploy.

**Both background services run in every instance.** `OutboxDispatcher` and `ReminderSweepService` are
hosted in the API. The sweep survives it — the reminder stamp and the row version mean a second
sweeper loses and retries — but the dispatcher has neither.

**Migrations apply at startup.** A bad migration takes the app down on boot rather than failing a
pipeline step, and the deployment has no way to stop before it.

## The peer critique

**An appointment booked less than a day ahead never gets a reminder.** `SweepRemindersHandler` works
out a single date, `now + leadTime`, and loads only that day's schedules. Book at 09:00 for noon the
same day and every later sweep is looking at tomorrow. The aggregate would stamp it correctly; the
sweep never hands it the day. This is the bug I would have hit first in real use.

**A message abandoned after five attempts is stamped processed.** `ProcessedAt` gets set either way,
so a confirmation that never sent is indistinguishable from one that did — for anyone except whoever
was reading the log at the time.

**Six tests, all domain.** Neither async claim the design makes is tested, and nor is a single route.
The two things most likely to be wrong are the two nobody is checking.

**The capstone is not in CI.** `ci.yml` builds `ThinkSchool.slnx`, which lists no DocBook project. It
is not that the tests fail; it is that nothing runs them. That gap is why the reminder bug survived.

## The ADR

[ADR 0001 — DoctorDaySchedule is the aggregate root, not Appointment](ADR-0001-doctor-day-schedule-is-the-aggregate-root.md).

I picked it because it is the decision everything else pays for. The modular monolith choice is
defensible in two sentences and nothing in the code argues with it. The aggregate choice is why the
read side skips the root, why the repository marks an unchanged root as modified, and why an
appointment cannot cross midnight. The design asserted it in one line without making the case; the
ADR is where the case goes.

## The top critique, and what it changed

The idempotency gap, over the reminder bug, because of what kind of fault it is. The reminder bug is
a bug inside a handler, and a bug has a fix. The other one is a design document stating a property
the system does not have, stated exactly where a reader would go to check it. Anyone writing the next
consumer would read that line, believe the platform handled duplicates, and write another handler
that sends twice. A wrong design document is worse than a missing one.

It changes the design in two places:

- **Notifications gets a schema of its own and a handled-message table**, keyed on the appointment id
  and the message kind. A second delivery finds the row and stops. This is a real change to what the
  module is: until now Notifications owned no data at all, and this is the first boundary in DocBook
  drawn for an operational reason rather than a domain one.
- **The dispatcher claims its batch** before working it, so two instances stop fighting over the same
  rows. Claiming narrows the duplicate window; the handled-message table is what closes it. Keeping
  both is deliberate — a claim is an optimisation, and correctness must never rest on one.

## The build plan

The four days are the ones the course sets, so the plan is what goes in each. Every day's exit
condition is a test or a command rather than an opinion.

**Day 29 — foundation and happy path against real infra.** Deploy the Bicep from Day 27, then drive
register → token → open a day → list free slots → book → confirmation against Azure SQL. The two
changes above land first, because the last leg of that path is the confirmation and it currently
sends twice. Three commits: the handled-message table, the claim, the deploy.
*Done when* the deployed URL answers `/health`, a booking round-trips against Azure SQL, and one
patient gets exactly one confirmation.

**Day 30 — feature completeness, behind a PR.** The reminder sweep covers the range from now to the
lead time instead of one date. Abandoned messages dead-letter instead of being stamped processed. A
notification sender that actually sends, with the logging one kept for development — a reminder
nobody receives is not a feature. Then the PR, and the comments worked properly rather than
force-pushed over.
*Done when* an appointment booked three hours out is reminded, a message that fails five times is
still findable, and the PR is reviewed.

**Day 31 — tests, perf, security, green gate.** DocBook into `ThinkSchool.slnx` first, so CI builds
it at all. Then the layers: the domain tests exist, integration tests over the HTTP surface with
`WebApplicationFactory` and Testcontainers, and one end-to-end booking against the deployed URL. The
two that matter most are the ones for the design's own claims — two parallel bookings for one slot
giving one success and one refusal, and an outbox message delivered twice sending once. Perf: p99 on
booking, the hottest write, from App Insights with the Day 26 queries. Security: the ZAP baseline
against the deployed HTTPS URL, which is where the HSTS rule Day 27 could not test finally gets
tested.
*Done when* the CI gate is green with DocBook in it and the p99 is written down.

**Day 32 — ship, demo, postmortem.** Public network access closed on the second deployment pass, the
demo driven through the happy path, and the one-page postmortem.

Both background services running in every instance is not on the plan. Claiming the batch makes it
safe to scale out; moving the services into their own worker is a bigger change than these four days
hold, and it belongs in the postmortem as the thing I would do next.
