# Day 29 — Build day 1: foundation and happy path

The capstone is [DocBook](../Capstone%20Project/DESIGN.md), a clinic appointment booking system. Today
was the foundation and the main flow, working against a real Azure SQL database rather than the local
container I had been using.

Repository: https://github.com/thinkbridge-thinkschool/ThinkSchool_Khushi_Jain

## The happy path

Six calls, and a seventh thing that is not a call:

1. `POST /api/v1/patients` — register. Always 202, so it never says whether the address was taken.
2. `POST /api/v1/tokens` — sign in as the patient.
3. `GET /api/v1/patients/me` — the token identifies the patient, so nothing carries an id in a body.
4. `POST /api/v1/tokens` — sign in as clinic staff, a different account with a different role.
5. `POST /api/v1/doctors/{doctorId}/days` — staff opens the doctor's day. A patient gets 403 here.
6. `GET .../free-slots` then `POST /api/v1/appointments` — the patient sees what is open and books.
7. The booking writes an integration event to the outbox in the same transaction. Seconds later the
   dispatcher picks it up and Notifications sends the confirmation.

## The commits

    1f5355f Drive the whole booking flow from one script
    99fb37e Read a patient's appointments through the schedule that owns them
    a75aca2 Let a deployment bring up the database on its own
    04713d3 Give the clinic desk an account the deployment can sign in with

Two of these are things the flow could not run without. **The clinic desk had no account** — the code
read `Staff:Id`, `Staff:Email` and `Staff:PasswordHash` from configuration and nothing set them
anywhere, so step 5 was impossible. They are now validated at startup, so a missing account stops the
app with a message instead of failing as an unexplained 401 halfway through the flow.

**Listing my appointments returned 500.** The read side asked EF for the shadow foreign key as a
`Guid`, but it is a `DoctorDayScheduleId` behind a value converter, and the shaper has no cast between
them. It now reads the doctor off the schedule row through the navigation, which is one query instead
of two and has no `EF.Property` in it.

The other two are how the day got to real infrastructure. The Bicep was one all-or-nothing template:
network, Key Vault, App Service plan, web app and two private endpoints alongside the database. A
`deployApplication` parameter now lets a deployment bring up the SQL server and database alone, which
is all the flow needs while the API runs on my machine. That is about 16 cents a day instead of $25 a
month, and the full topology is still the default for the day I ship it live.

## Running it

Deploy the database, open the firewall for this machine, and make me the server's Entra administrator:

```bash
az group create --name rg-docbook --location koreacentral
```

```bash
DOCBOOK_ADMIN_OBJECT_ID="$(az ad signed-in-user show --query id -o tsv)" DOCBOOK_PUBLIC_ACCESS=Enabled DOCBOOK_DEPLOY_APPLICATION=false DOCBOOK_DEVELOPER_IP="$(curl -s https://api.ipify.org)" az deployment group create --resource-group rg-docbook --name main --parameters "Capstone Project/infra/main.bicepparam"
```

Hash a password for the clinic desk account. The password stays in the shell:

```bash
STAFF_HASH="$(dotnet run "Capstone Project/infra/staff-password-hash.cs" -- 'clinic-desk-password' | tail -1)"
```

Run the API against the deployed database. There is no SQL login on that server, so the connection
authenticates as whoever is signed in to `az`:

```bash
ConnectionStrings__DocBook="Server=tcp:$(az deployment group show --resource-group rg-docbook --name main --query properties.outputs.sqlServerFullyQualifiedDomainName.value -o tsv),1433;Database=docbook;Encrypt=True;TrustServerCertificate=False;Authentication=Active Directory Default" Jwt__SigningKey="$(openssl rand -base64 32)" Staff__Id='4f2c9a17-6b3e-4d81-9c05-2ae7f1b8d640' Staff__Email='desk@docbook.example' Staff__PasswordHash="$STAFF_HASH" dotnet run --project "Capstone Project/src/DocBook.Api"
```

Both migration sets apply at startup, creating the `patients` and `scheduling` schemas. Then, in
another terminal:

```bash
STAFF_PASSWORD='clinic-desk-password' bash "Capstone Project/happy-path.sh"
```

## The walkthrough

Every call, with the tokens cut short and the free-slot list trimmed:

```
POST /api/v1/patients -> 202

POST /api/v1/tokens -> 200
{"access_token":"eyJhbGciOiJIUzI1NiIs...","token_type":"Bearer","expires_in":900}

GET /api/v1/patients/me -> 200
{"patientId":"01a0a603-ed17-7156-9d7e-b7aa2ab8295e","fullName":"Walkthrough Patient",...}

POST /api/v1/tokens -> 200
{"access_token":"eyJhbGciOiJIUzI1NiIs...","token_type":"Bearer","expires_in":900}

POST /api/v1/doctors/a992d15e-5d55-68a9-58ef-ef9e8fd867d9/days -> 201
{"scheduleId":"01a0a603-f642-7800-9bb2-3b6c822ae63f"}

GET /api/v1/doctors/a992d15e-.../days/2026-09-16/free-slots?minutes=30 -> 200
{"doctorId":"a992d15e-...","date":"2026-09-16","slots":[{"start":"2026-09-16T09:00:00+00:00","end":"2026-09-16T09:30:00+00:00"}, ... 16 slots]}

POST /api/v1/appointments -> 201
{"appointmentId":"01a0a603-fd98-798c-b9e2-6d821cc63ce4"}

GET /api/v1/appointments/mine -> 200
{"page":1,"size":20,"items":[{"appointmentId":"01a0a603-fd98-798c-b9e2-6d821cc63ce4","doctorId":"a992d15e-5d55-68a9-58ef-ef9e8fd867d9","start":"2026-09-16T09:00:00+00:00","end":"2026-09-16T09:30:00+00:00","status":"Booked","reason":"Routine consultation"}]}
```

The seventh step is in the API's own log, a few seconds after the booking:

```
info: DocBook.Notifications.Infrastructure.LoggingNotificationSender[0]
      Notification Your appointment is confirmed queued for patient 01a0a603-ed17-7156-9d7e-b7aa2ab8295e.
```

That is the whole async path proven: a domain event raised by the aggregate, an integration event
written to the outbox in the booking's transaction, the dispatcher's next pass, and a handler that
reached Patients through `IPatientDirectory` without touching its tables.

## What I learned this session

The write side and the read side fail in different places: the aggregate saved fine because EF applies
the value converter to a mapped property, and the query broke because `EF.Property<Guid>` on a shadow
foreign key asks for a type the converter never produces.

## What would break this

The confirmation is a log line. `LoggingNotificationSender` writes and returns, so "the patient was
notified" is not yet true for any patient — nothing leaves the process. The times are also all UTC,
so a clinic that opens at nine o'clock local would open at the wrong hour.
