# DocBook — design

DocBook is a clinic appointment booking system. A clinic opens a doctor's day, patients book into
it, and everyone involved gets told what happened.

One deployable app split into modules, not microservices: one clinic, one database, no need for
independent deployment. The modules meet only at published contracts, so the seam is already cut if
one ever has to move out.

## Bounded contexts

| Context | Owns | Reached by |
| --- | --- | --- |
| **Scheduling** | Doctor day schedules, appointments, the booking rules | Publishes integration events |
| **Patients** | Patient identity and contact details | Exposes `IPatientDirectory` in its contracts |
| **Notifications** | Confirmations and reminders, and the log of which have been sent | Consumes Scheduling's events, publishes none |

Each context owns its own schema — `scheduling`, `patients`, `notifications` — and no query crosses one. `PatientId`
is a separate type in each: Scheduling stores a reference, Patients owns the record behind it.

## Core aggregate: DoctorDaySchedule

`Appointment` is the obvious root and the wrong one. The rule that matters most is that a doctor is
never double-booked, and that rule spans two appointments — so the root is the day, not the booking.

`DoctorDaySchedule` is one doctor, one date, that day's opening hours, and the appointments in it.
`Appointment` is an entity inside it; `TimeSlot` is a value object. Every write goes through the root:

```
Open(doctorId, date, opensAt, closesAt)
Book(patientId, slot, reason, now) -> Appointment
Cancel(appointmentId, reason, now)
MarkRemindersDue(now, leadTime)
```

Invariants:

- The slot starts in the future and ends after it starts.
- It falls on the schedule's date and inside the doctor's opening hours.
- It overlaps no active appointment that day.
- Only an active appointment that has not started can be cancelled.

Two people booking the same doctor load the same root, so an optimistic concurrency check makes the
second write fail. The rule lives in the domain, not in a unique index. Booking inserts a child row
without touching the root's, so the repository marks the root modified to force that check.

Reads go the other way: a patient's upcoming appointments span many roots, so the read side queries
the appointments table directly.

## Async flows

Aggregates raise **domain events**, handled inside Scheduling in the same transaction. Those handlers
write **integration events** to a transactional outbox in the `scheduling` schema — same transaction
as the booking, so nothing is lost if the process dies. A background dispatcher then delivers them,
claiming each batch on a short lease first so two instances never work the same row and an instance
that dies hands its rows back when the lease expires.

1. **Confirmation** — `Book` raises `AppointmentBooked` → outbox → Notifications looks up the contact
   through `IPatientDirectory` and sends it.
2. **Cancellation** — `Cancel` raises `AppointmentCancelled` → outbox → Notifications tells the patient.
3. **Day-before reminder** — a scheduled job sweeps tomorrow's schedules and calls
   `MarkRemindersDue`, which raises one `AppointmentReminderDue` per due appointment and stamps it so
   it fires once → outbox → Notifications sends it.

Delivery is at-least-once, so handlers are idempotent: Notifications keeps a `handled_messages` table
keyed on the appointment id and the message kind, checks it before sending, and writes to it after. A
redelivered message finds the row and stops.

## Scaffolded solution layout

```
DocBook.slnx
Directory.Build.props                        settings every project shares
src/
  DocBook.Api/                             the one deployable: composition root
  DocBook.SharedKernel/                    Entity, AggregateRoot, event markers, DomainException
  DocBook.Infrastructure/                  shared adapters: outbox table, outbox dispatcher
  Modules/
    Scheduling/
      DocBook.Scheduling.Contracts/        integration events others may subscribe to
      DocBook.Scheduling.Domain/           DoctorDaySchedule, Appointment, TimeSlot, repositories
      DocBook.Scheduling.Application/      OpenDoctorDay, BookAppointment, CancelAppointment
      DocBook.Scheduling.Infrastructure/   EF Core mapping, repositories, endpoints, reminder job
    Patients/
      DocBook.Patients.Contracts/          IPatientDirectory
      DocBook.Patients.Domain/             Patient
      DocBook.Patients.Application/        RegisterPatient
      DocBook.Patients.Infrastructure/     EF Core mapping, repository, endpoints
    Notifications/
      DocBook.Notifications.Application/   handlers for Scheduling's integration events
      DocBook.Notifications.Infrastructure/ the sender, the handled-message log, module registration
tests/
  DocBook.Scheduling.Domain.Tests/         the aggregate's invariants, no database
```

All three modules map to one SQL Server database, each into its own schema and with its own migration
history table. `DocBook.Api` applies all three migration sets at startup.

Every route but `/health` needs a bearer token. The caller's identity is built from that token and
passed to the handlers as an `Actor`, so no use case takes an identity from a request body. There are
two roles: a patient, who acts only for themselves, and clinic staff, who open days and may act for
anyone. Staff is a single account read from configuration, since the design has no staff aggregate.

Dependencies run inward. `Domain` sees only the shared kernel. `Application` sees its own `Domain`
plus other modules' `Contracts`. `Infrastructure` sees its own `Application`, the shared
`DocBook.Infrastructure`, and is the only place EF Core or HTTP appears. `DocBook.Api` sees each
module's `Infrastructure` and the shared one, and no module directly.

The rule that keeps this modular is about project references: **no module may reference another
module's Domain, Application, or Infrastructure — contracts only.** A wrong reference fails the
build, so the compiler enforces the boundary instead of discipline.

## Build and run

```bash
dotnet build "Capstone Project/DocBook.slnx"
dotnet test "Capstone Project/DocBook.slnx"
```

Running needs SQL Server and two environment variables; the steps are in
[day27_security/README.md](../day27_security/README.md).

## Known limits

- Times are UTC. Local opening hours across a daylight-saving change need a real time zone.
- An appointment cannot cross midnight, because the aggregate is one doctor for one date.
- The outbox is polled, so a confirmation lands seconds after the booking.
- `IPatientDirectory` is a synchronous call from Scheduling into Patients — the one request-time
  coupling between modules.
- Clinic staff is one account from configuration, so every member of staff shares an actor id.
- Access tokens only, so signing out means waiting for the token to expire.
- A signed-in patient can read any doctor's free slots, which is also how they learn a doctor is busy.
