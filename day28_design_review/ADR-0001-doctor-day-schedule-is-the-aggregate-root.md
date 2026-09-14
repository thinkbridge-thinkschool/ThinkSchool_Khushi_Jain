# ADR 0001 — DoctorDaySchedule is the aggregate root, not Appointment

Status: accepted, 14 September 2026.

## Context

DocBook books patients into a doctor's day. The rule the product rests on is that a doctor is never
in two places at once: no two active appointments for the same doctor may overlap. Opening hours,
cancellation windows and reminders all matter less than that one.

An aggregate is the unit a write loads, locks and checks. Its boundary has to contain every piece of
state a rule reads, or the rule cannot be enforced without reaching outside the boundary — and that
is where races live. Choosing the aggregate is choosing which rules can be enforced honestly.

"No overlap" reads two appointments at once. A single `Appointment` cannot see its neighbours, so it
cannot enforce the rule that matters most.

## Decision

The root is `DoctorDaySchedule`: one doctor, one date, that day's opening hours, and every
appointment in it. `Appointment` is an entity inside that boundary and `TimeSlot` is a value object.
Every write goes through the root — `Open`, `Book`, `Cancel`, `MarkRemindersDue` — so a booking
loads the doctor's whole day and the overlap check reads state the boundary already owns.

The date is the boundary because a doctor's day is the smallest span that still contains the rule.
An appointment never crosses midnight, so no overlap can straddle two roots.

## Alternatives

**`Appointment` as the root, with uniqueness in the database.** A unique index on doctor and start
time. It is the obvious model and it does not work: an index on the start instant catches only exact
collisions, so 09:00–09:30 and 09:15–09:45 both pass. Overlap needs a range constraint, and SQL
Server has none. Even if it had one, a constraint is not a domain rule — the model would no longer
say why a booking is refused, the rule could not be tested without a database, and the application
would learn about it as a `SqlException` carrying an index name.

**`Appointment` as the root, with a lock per doctor.** Serialise every booking for one doctor. This
puts the rule in a lock rather than in a type, holds that lock across a request, and picks a grain
coarser than the rule needs: two bookings on different dates for one doctor would queue behind each
other for no reason.

**`Doctor` as the root, holding every day.** Correct and unbounded. The aggregate grows for as long
as the clinic operates, and every booking loads years of history to check one morning.

## Consequences

What it buys:

- The rule is a line of code in the domain and is tested without a database.
- The boundary is the right size for contention. Two people booking the same doctor on the same day
  contend; two people booking different days do not.
- Cancelling, stamping reminders and listing free slots all read the same day and need no extra query.

What it costs, accepted:

- **Reads go around the root.** A patient's upcoming appointments span many doctors and many days, so
  the read side queries the appointments table directly rather than loading roots. One table, two
  routes.
- **The concurrency check needs propping up.** Booking only inserts a child row, so EF Core sees the
  root as unchanged and skips its `rowversion` check, and two concurrent bookings both commit. The
  repository marks the root modified to force the check. That is the aggregate's price, paid in the
  persistence layer, and it is one line somebody could delete with no failing test to stop them.
- **No appointment crosses midnight**, because a slot must fall entirely on the root's date. Right for
  a clinic, wrong for anything overnight.
- **A day must be opened before it can be booked.** A clinic that forgets to open Tuesday looks closed.

Revisit if appointments have to span midnight, or if one doctor-day gets hot enough that per-day
contention hurts. The first breaks the boundary outright. The second would want a finer root, and
there is no finer one that still contains the overlap rule.
