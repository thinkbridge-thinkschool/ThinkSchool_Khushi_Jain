# Day 30 — Build day 2: feature completeness

Pull request: https://github.com/thinkbridge-thinkschool/ThinkSchool_Khushi_Jain/pull/1

Feature completeness for [DocBook](../Capstone%20Project/DESIGN.md) did not mean new endpoints. The
HTTP surface was already whole — register, sign in, open a doctor's day, list free slots, book,
cancel, list my appointments. What was missing were the five places where `DESIGN.md` promised
something the code did not do.

## The five gaps

    5a1384f Send a patient one notification of a kind per appointment
    f6b2ddf Claim an outbox batch before working it
    37f5d2e Stamp an outbox message abandoned rather than processed
    050aa9f Remind an appointment booked inside the lead time
    0732c32 Send a notification instead of logging that one would be sent

**The design claimed idempotent handlers and there were none.** Notifications now owns a
`notifications` schema and a `handled_messages` table keyed on the appointment id and the message
kind. It is the first data that module owns, and the first boundary in DocBook drawn for an
operational reason rather than a domain one.

**Nothing claimed an outbox row.** The dispatcher read pending rows with no lease, so two instances
delivered the same batch every ten seconds. It now claims a batch for two minutes using
`ROWLOCK, READPAST, UPDLOCK`, so a second instance skips held rows instead of waiting on them.

**An abandoned message was stamped processed**, which made a confirmation that never sent look
exactly like one that did. It gets its own `AbandonedAt` now and stays findable.

**The reminder sweep loaded one date.** Book at 09:00 for noon the same day and every later sweep was
looking at tomorrow, so that appointment was never reminded. It sweeps the range from today to the
lead time now. The aggregate was always right; the sweep never handed it today's schedules.

**Nothing was actually sent.** There is an Azure Communication Services sender now, with the logging
one kept for local work. A booking against the deployed database produced a real confirmation email.

## The review

Six comments on the diff, three of which I took and three I did not. The whole exchange is on the PR;
the one I would point at is the
[ordering of the send and the record](https://github.com/thinkbridge-thinkschool/ThinkSchool_Khushi_Jain/pull/1#discussion_r4023290553).

**Changed.** The `catch (DbUpdateException)` when recording a handled message caught every failure,
not only the duplicate key, so a command timeout would have been reported as a recorded message and
the confirmation lost. Narrowed to SQL Server 2627 and 2601 in `8313d33`. `ClaimDuration` had quietly
become the retry backoff without saying so and was the one outbox setting missing from
`appsettings.json`; both fixed in `7c32a35`. The email sender threw away the operation id, which was
the only handle on a send that fails after acceptance; kept and logged in `95d1620`.

**Defended.** Recording the handled message *before* sending closes the crash window and opens a
worse one — a failed send then leaves a row saying the patient was told, and they never hear
anything. Two emails beat none. The raw SQL stays because `READPAST` is the mechanism and not an
optimisation: without it the second instance blocks rather than skips, and EF Core cannot emit a
table hint through LINQ. Choosing the sender at startup stays because that is what gives
`ValidateOnStart` something to validate — resolving per send moves a misconfiguration from a refused
boot to a failed handler inside a background dispatcher.

CI does not build any of this. `ci.yml` builds `ThinkSchool.slnx`, which lists no DocBook project, so
the green check on the PR only covers unrelated projects. I said so in the PR description rather than
let the check imply more than it means. Putting DocBook in that solution is tomorrow's first task.

## What I learned this session

The catch I wrote and the reason I gave for it were not the same statement. "The composite key is the
only constraint on this table" is true; "so any failure here is that constraint" does not follow, and
a timeout would have been recorded as a message successfully sent. Reading my own comment caught it,
not reading the code.

## What would break this

Nothing in DocBook knows whether a patient received anything. Communication Services accepting the
request is the last thing we see, so a bounce or a rejected recipient is invisible here — the
operation id in the log is the only way to find one, and only in Azure.
