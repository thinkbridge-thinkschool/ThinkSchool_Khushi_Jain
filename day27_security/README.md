# Day 27 — Security pass on the capstone

The capstone is [DocBook](../Capstone%20Project/DESIGN.md), a clinic appointment booking system. This
day is a security pass over it: a STRIDE-lite threat model, the data tier moved behind private
endpoints, the HTTP surface hardened, and a ZAP baseline scan.

DocBook was a scaffold when I started — the aggregates, the use cases and the module wiring, but no
endpoints, no database and no authentication. There was nothing to put behind a private endpoint and
nothing for a scanner to scan, so this day also builds the adapters the design had been leaving for
later. The security work is what shaped them.

## The threat model

[THREAT-MODEL.md](THREAT-MODEL.md). The short version is that `Appointment.Reason` is a medical
complaint and `Patient` is the record that attaches it to a named person, so the model is mostly
about keeping those two apart from each other and from everybody else.

Three things the design already had right, which I kept: `IPatientDirectory` hands out a name and an
email and nothing else, no integration event carries the medical reason, and the project reference
rule means Notifications physically cannot read the `patients` tables.

## What I built

**Persistence.** A `DbContext` per module, each owning its own schema and its own migration history
table, both on one database. `scheduling` holds the schedules, the appointments, the outbox and an
audit trail; `patients` holds the patient records and its own audit trail. Domain events are
dispatched before the save, so the outbox row and the booking that caused it commit together. The
dispatcher is shared but reaches the table through `IOutboxStore`, so the module keeps its schema.

**A concurrency token.** The design says the aggregate is what prevents double booking, but two
concurrent requests each load the schedule, each see the slot free, and each insert. A `rowversion`
on the schedule fixes it — except that booking only inserts a child row, so the repository marks the
root modified to force the check. The second writer now loses and gets the same "not available"
answer as any other taken slot.

**The HTTP surface.** Endpoints for the four use cases plus the two reads, all under `/api/v1`, with
an OpenAPI document that declares the bearer scheme rather than leaving a caller to discover it by
being refused. The document itself needs a token: it describes every route in one response.

**Authentication and roles.** Bearer tokens, fifteen minutes, no refresh token. Two roles: a patient
and clinic staff. Staff is one account from configuration, because the design has no staff
aggregate and inventing one was not this day's work.

**Identity comes from the token.** This was the finding I cared most about. `BookAppointment` took
`PatientId` as a field on the command, so a login alone would have changed nothing — the handler
would still believe whichever id the body carried. Every handler now takes an `Actor` built from the
validated token, and a patient booking or cancelling for anybody but themselves is refused.

## Closing the oracles

Three places answered questions the caller had not earned.

Booking told you when a doctor was busy: "The doctor already has an appointment in that slot" is a
fact about somebody else's visit. Closed hours and a taken slot now share one code and one message,
so a refusal cannot be read either way, and `GET .../free-slots` is the proper way to ask — signed
in, because the complement of a free slot is a busy one.

Cancelling told you an appointment existed: someone else's returned a different status from one that
was never there. Both now answer 404.

Registering told you an address was taken. It now always returns 202, so a duplicate creates nothing
and says nothing. Sign-in verifies against a throwaway hash when the address is unknown, so a wrong
address costs about the same time as a wrong password.

Rule messages no longer reach the caller at all. `DomainException` carries a code, and the host maps
codes to fixed titles and logs the detail.

## Input limits

| Limit | Where |
| --- | --- |
| 32 KB request body | Kestrel, down from the 30 MB default |
| 5 requests a minute on registration and sign-in | `RateLimits.Sensitive` |
| 120 requests a minute otherwise, per caller | The global limiter |
| Lengths on every string field | Annotations on the request records |
| Page size capped at 50 | `SchedulingController` |
| Slot length between 5 and 240 minutes | `SchedulingController` |
| Reminder sweep paged | `SweepRemindersHandler` |

## The private-endpoint change

[`Capstone Project/infra/`](../Capstone%20Project/infra/). The SQL server is created with
`publicNetworkAccess: 'Disabled'` and reached only through a private endpoint in the `data` subnet,
with a `privatelink.database.windows.net` zone linked to the network so the server's normal host name
resolves to the private address. Key Vault, which holds the token signing key, is behind the same
arrangement.

The half that is easy to miss is the caller. A private endpoint does nothing unless the app is on the
network, so the API runs on an App Service plan with regional VNet integration into a delegated `app`
subnet and `vnetRouteAllEnabled`, which is what sends its database traffic through the VNet instead of
out to the public name. B1 is the cheapest tier that supports this.

`publicNetworkAccess` is a parameter for one reason: turning it off closes the routes two one-time
setup steps need. The database grant has to run as the Entra administrator from outside the network,
and writing the signing key into the vault is a data-plane call. Both happen on the first deploy,
with the flag on; the second deploy turns it off and neither is ever needed again.

**Not deployed yet** — this goes up on a new Azure subscription. The steps below are the whole of it,
from the repository root, one line each.

```bash
az login
```

```bash
az group create --name rg-docbook --location eastus
```

```bash
DOCBOOK_ADMIN_OBJECT_ID="$(az ad signed-in-user show --query id -o tsv)" DOCBOOK_PUBLIC_ACCESS=Enabled az deployment group create --resource-group rg-docbook --name main --parameters "Capstone Project/infra/main.bicepparam"
```

```bash
bash "Capstone Project/infra/grant-sql-access.sh" rg-docbook
```

```bash
az keyvault secret set --vault-name "$(az deployment group show --resource-group rg-docbook --name main --query properties.outputs.keyVaultName.value -o tsv)" --name 'Jwt--SigningKey' --value "$(openssl rand -base64 32)"
```

```bash
dotnet publish "Capstone Project/src/DocBook.Api" -c Release -o publish && powershell -Command "Compress-Archive -Path publish/* -DestinationPath docbook.zip -Force" && az webapp deploy --resource-group rg-docbook --name "$(az deployment group show --resource-group rg-docbook --name main --query properties.outputs.apiName.value -o tsv)" --src-path docbook.zip --type zip
```

Then the same deployment again without `DOCBOOK_PUBLIC_ACCESS`, which is what actually closes the
public endpoints:

```bash
DOCBOOK_ADMIN_OBJECT_ID="$(az ad signed-in-user show --query id -o tsv)" az deployment group create --resource-group rg-docbook --name main --parameters "Capstone Project/infra/main.bicepparam"
```

Roughly $25 a month while it exists: about $13 for the B1 plan, $5 for SQL Basic, and $7 for each of
the two private endpoints. `az group delete --name rg-docbook` takes all of it away.

## Running it

One line each, from the repository root. The migrations are generated once, and the design-time
factories mean neither command touches a database:

```bash
dotnet ef migrations add InitialCreate --project "Capstone Project/src/Modules/Patients/DocBook.Patients.Infrastructure" --startup-project "Capstone Project/src/DocBook.Api" --context PatientsDbContext
```

```bash
dotnet ef migrations add InitialCreate --project "Capstone Project/src/Modules/Scheduling/DocBook.Scheduling.Infrastructure" --startup-project "Capstone Project/src/DocBook.Api" --context SchedulingDbContext
```

Then SQL Server — the password lives in the shell, not in the repository:

```bash
MSSQL_SA_PASSWORD="$(openssl rand -base64 24)aA1" docker compose -f "Capstone Project/docker-compose.yml" up -d
```

Then export the connection string and the signing key, and run the API:

```bash
export ConnectionStrings__DocBook="Server=localhost,11434;Database=docbook;User Id=sa;Password=$MSSQL_SA_PASSWORD;Encrypt=True;TrustServerCertificate=True" && export Jwt__SigningKey="$(openssl rand -base64 32)" && dotnet run --project "Capstone Project/src/DocBook.Api"
```

Migrations apply at startup. The API listens on `http://localhost:5205`, and `GET /health` is the one
route that needs no token.

## The ZAP baseline

```bash
./day27_security/zap-baseline.sh
```

The baseline scan is passive: it crawls and reads what comes back, and sends no attack payload. It is
looking for what the responses give away — missing headers, cacheable sensitive responses, a server
banner, cookie flags.

I wrote the fixes for that category before running it, since they are predictable:

| What a baseline scan looks for | What the API sends | Rule |
| --- | --- | --- |
| Anti-clickjacking | `X-Frame-Options: DENY` and `frame-ancestors 'none'` | 10020 |
| MIME sniffing | `X-Content-Type-Options: nosniff` | 10021 |
| Content security policy | `default-src 'none'`, since a JSON API renders nothing | 10038, 10055 |
| Referrer leakage | `Referrer-Policy: no-referrer` | 10025 |
| Cacheable responses | `Cache-Control: no-store` on everything | 10015 |
| Server banner | Kestrel's server header is off | 10036 |
| Framework version banner | No `X-Powered-By`, no `X-AspNet-Version` | 10037, 10061 |
| Feature policy | Geolocation, camera and microphone denied | 10063 |

## What the scan found

`FAIL-NEW: 0, WARN-NEW: 1, PASS: 66`. Every header rule above passed, and those passes are real:
the middleware runs on every response including a 404, so the scanner saw the headers on everything
it touched.

The one warning is **Non-Storable Content [10049]**, twice, on `/` and `/sitemap.xml`. It fires
because the responses cannot be held in a cache — which is exactly what `Cache-Control: no-store` is
for. It is the scanner noticing a control I added on purpose, so there is nothing to fix.

Two things I am not going to dress up.

**The spider reached three URLs, and all three were 404.** DocBook has no route at `/`, no HTML and
no links, so a crawler starting at the root has nothing to follow. Every route but `/health` needs a
bearer token, and the OpenAPI document needs one too, so an anonymous scanner cannot discover them.
That is the design working — the surface visible without a token really is three not-found responses
— but it means 66 passes is a statement about the error responses, not about the booking endpoints.

**Strict-Transport-Security [10035] passed without being tested.** The scan ran over HTTP against
localhost, and ZAP only evaluates that rule on HTTPS. The API sends no HSTS header; in Azure it is
`httpsOnly` on the App Service that forces TLS instead. Scanning the deployed HTTPS URL would flag
it properly.

A baseline scan is the right instrument for what it measures — what responses give away — and it
says the headers are right. It is not evidence the authenticated surface is sound. The controls
there are the ones in the threat model, and it is the code that argues for them, not this report.

Against the deployed API instead of localhost:

```bash
./day27_security/zap-baseline.sh "https://$(az deployment group show --resource-group rg-docbook --name main --query properties.outputs.apiHostName.value -o tsv)"
```

The full report is [`zap-report.html`](zap-report.html).
