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
| **Notifications** | Confirmations and reminders | Consumes Scheduling's events, publishes none |

Each context owns its own schema — `scheduling`, `patients` — and no query crosses one. `PatientId`
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
second write fail and retry. The rule lives in the domain, not in a unique index.

Reads go the other way: a patient's upcoming appointments span many roots, so the read side queries
the appointments table directly.

## Async flows

Aggregates raise **domain events**, handled inside Scheduling in the same transaction. Those handlers
write **integration events** to a transactional outbox in the `scheduling` schema — same transaction
as the booking, so nothing is lost if the process dies. A background dispatcher then delivers them.

1. **Confirmation** — `Book` raises `AppointmentBooked` → outbox → Notifications looks up the contact
   through `IPatientDirectory` and sends it.
2. **Cancellation** — `Cancel` raises `AppointmentCancelled` → outbox → Notifications tells the patient.
3. **Day-before reminder** — a scheduled job sweeps tomorrow's schedules and calls
   `MarkRemindersDue`, which raises one `AppointmentReminderDue` per due appointment and stamps it so
   it fires once → outbox → Notifications sends it.

Delivery is at-least-once, so handlers are idempotent: Notifications keys on the appointment id and
the message kind.

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
      DocBook.Notifications.Infrastructure/ the sender, module registration
tests/
  DocBook.Scheduling.Domain.Tests/         the aggregate's invariants, no database
```

What is scaffolded so far: the projects and their references, the aggregate and its invariants, the
use cases, the contracts each module publishes, and each module's registration into the host. The
adapters named in the tree — EF Core mapping, repositories, endpoints, the outbox dispatcher — are
the next piece of work.

Dependencies run inward. `Domain` sees only the shared kernel. `Application` sees its own `Domain`
plus other modules' `Contracts`. `Infrastructure` sees its own `Application` and is the only place
EF Core or HTTP appears. `DocBook.Api` sees each module's `Infrastructure` and nothing else.

The rule that keeps this modular is about project references: **no module may reference another
module's Domain, Application, or Infrastructure — contracts only.** A wrong reference fails the
build, so the compiler enforces the boundary instead of discipline.

## Build

```bash
dotnet build "Capstone Project/DocBook.slnx"
dotnet test "Capstone Project/DocBook.slnx"
```

## Known limits

- Times are UTC. Local opening hours across a daylight-saving change need a real time zone.
- An appointment cannot cross midnight, because the aggregate is one doctor for one date.
- The outbox is polled, so a confirmation lands seconds after the booking.
- `IPatientDirectory` is a synchronous call from Scheduling into Patients — the one request-time
  coupling between modules.
