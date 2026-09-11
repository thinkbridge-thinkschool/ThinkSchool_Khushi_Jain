# DocBook — threat model (STRIDE-lite)

DocBook is a clinic appointment booking system. That makes it a different kind of target from the
rest of this repository: a quote is public by nature, but why someone is seeing a doctor is not.
`Appointment.Reason` is free text describing a medical complaint, and `Patient` holds a name, an
email address and a phone number. Everything below is ordered around keeping those two out of the
wrong hands.

I modelled the design as it is meant to run — one deployable, three modules, a SQL database, an
outbox and a background dispatcher — not only the part that is already built, because the point of
doing this first is to decide what the rest of Day 27 builds.

## What is worth stealing

| Asset | Where it lives | Why it matters |
| --- | --- | --- |
| The reason for an appointment | `Appointment.Reason`, `scheduling` schema | Health information about a named person |
| Patient contact details | `Patient`, `patients` schema | Identifies the person the reason belongs to |
| A doctor's day | `DoctorDaySchedule` | Who is seeing patients when, and how busy a clinic is |
| Outbox payloads | `OutboxMessage.Payload` and `.Error` | Serialised copies of the above, in a second table |
| The database credential | App configuration | One value that reaches all of it |

The first two are only dangerous together. A reason with no patient attached is close to harmless,
which is why the module boundary below matters as much as any access check.

## Trust boundaries

1. **Internet to the API.** Every request arrives here. Today nothing authenticates.
2. **The API to the database.** Currently would cross the public internet. This is the boundary the
   private endpoint closes.
3. **Scheduling to Patients.** In process, but a real boundary: Scheduling may only read a patient
   through `IPatientDirectory`, and the compiler stops it reaching further.
4. **The outbox to Notifications, and Notifications to the outside.** Where clinic data leaves the
   system as a message.
5. **Me to Azure.** Deployment credentials and the one-time database grant.

## S — Spoofing

| Threat | Today | Fix |
| --- | --- | --- |
| Anyone can act as any patient | No authentication exists | Authentication on every route but `/health` |
| A caller books on someone else's behalf | `BookAppointment` takes `PatientId` as a field on the command | Take the patient from the validated token, never from the request body |
| A caller opens a doctor's day | `OpenDoctorDay` has no notion of who is asking | Restrict it to a clinic staff role |

The second row is the one that would survive a careless fix. Adding a login does not help while the
handler still believes whichever `PatientId` the body contains.

## T — Tampering

| Threat | Today | Fix |
| --- | --- | --- |
| Cancelling another patient's appointment | `CancelAppointment` checks the domain rules but never ownership | Authorize the caller against the appointment's patient, staff excepted |
| Guessing appointment ids | Ids are `Guid.CreateVersion7()`, which is time-ordered, so ids issued near each other sort together | Keep the ids, but make ownership the control rather than obscurity |
| Two bookings taking the same slot | The aggregate makes double booking impossible in memory, but nothing enforces it across two concurrent transactions yet | A `rowversion` concurrency token on `DoctorDaySchedule`, so the second write fails and retries |

## R — Repudiation

Nothing records who did anything. `AppointmentCancelled` carries a reason but no actor, so a patient
who says "I never cancelled that" cannot be contradicted, and neither can a member of staff. In a
clinic that is a dispute with consequences.

The fix is an actor on the cancel command and an audit record written in the same transaction as the
change: who, what, when, from where.

## I — Information disclosure

This is the largest category and the design already gets part of it right.

What is already good, and I am keeping:

- `IPatientDirectory` returns an id, a name and an email — never the phone number, never the whole
  record. Notifications cannot read more than it needs.
- The medical reason stays inside the Scheduling aggregate. No integration event carries it, so it
  never reaches the outbox, Notifications, or anything downstream.
- `LoggingNotificationSender` logs a subject and a patient id, not the message body or the address.

What is not:

| Threat | Today | Fix |
| --- | --- | --- |
| The cancellation reason leaks | It is caller-supplied free text, and it does travel — into `AppointmentCancelledIntegrationEvent`, then the outbox `Payload`, then the `Error` column if a dispatch fails | Cap and validate it, and keep payloads out of error text |
| Booking collisions confirm a doctor's schedule | `Book` throws "The doctor already has an appointment in that slot", which answers a question the caller was not entitled to ask | Return the same response for a taken slot as for an unavailable one, and publish free slots explicitly instead |
| Patient enumeration through booking | `BookAppointmentHandler` returns null for an unknown patient and throws for a broken rule, so the two are distinguishable from outside | One response for "cannot book that", whichever of the two it was |
| Account enumeration through registration | Registering against a taken address would answer differently from a free one, which says whether somebody is a patient of this clinic | Always accept the registration; a duplicate creates nothing and says nothing |
| The database is publicly reachable | It would be, on a default Azure SQL server | Private endpoint, public network access disabled |
| Rule text reaches the caller | `DomainException` messages are written for developers | Map them to a problem response with a fixed title, and log the detail |

## D — Denial of service

| Threat | Today | Fix |
| --- | --- | --- |
| Unlimited patient registration | No authentication and no throttle on the write | Rate limit the unauthenticated routes hardest |
| Oversized request bodies | No cap beyond Kestrel's 30 MB default, and no length limit on the reason or the name | Request body limit plus length caps in the contracts |
| The reminder sweep grows without bound | `MarkRemindersDue` walks schedules with nothing limiting how many | Sweep in batches, one page at a time |
| Retry storms on a contended slot | Optimistic concurrency retries amplify under load | Bound the retries |

## E — Elevation of privilege

DocBook has at least three kinds of actor — a patient, clinic staff, and a doctor — and today the
system cannot tell them apart, because it has no roles at all. Opening a day is staff work. Booking
and cancelling belong to the patient the appointment is for, or to staff acting for them. Reading
somebody else's appointments belongs to nobody.

The structural defence already in place is the project reference rule: no module may reference
another module's Domain, Application or Infrastructure. A wrong reference fails the build, so
Notifications physically cannot read the `patients` tables even if its code tried. That is
containment enforced by the compiler rather than by review, and it limits how far a flaw in one
module reaches.

## What Day 27 closes

| Finding | Where it is fixed |
| --- | --- |
| No authentication, no roles, identity taken from the request body | `AuthenticationExtensions`, `Actor`, every handler signature |
| No ownership check on cancel | `CancelAppointmentHandler` |
| No concurrency token on the aggregate | `SchedulingDbContext`, `DoctorDayScheduleRepository` |
| No audit record of who changed what | `AuditEntry` and the two `IAuditTrail` adapters |
| Collision and enumeration oracles | `DomainException` codes, `ProblemExtensions`, the free-slots route |
| Rule text reaching the caller | `ProblemExtensions` |
| No rate limits, no size or length caps | `RateLimitingExtensions`, Kestrel limits, the request records |
| The reminder sweep growing without bound | `SweepRemindersHandler`, paged |
| Database reachable from the public internet | `infra/main.bicep` |
| Whatever the scan finds | ZAP baseline |

Two things stay open and are worth naming. Free slots are readable by any signed-in patient, and the
complement of a free slot is a busy one — a patient of the clinic can therefore see when a doctor is
occupied, though never by whom. And staff is a single account from configuration rather than a
managed set of people, so every member of staff shares one actor id in the audit trail.
