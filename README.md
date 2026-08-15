# ThinkSchool — Khushi Jain

A quotes API built across the Thinkbridge Day 1–5 programme: ASP.NET Core 10 minimal APIs, EF Core,
self-issued JWT plus Entra ID authentication, structured logging, distributed tracing, and deployment to
Azure Container Apps.

## Projects

| Path | What it is |
|---|---|
| `QuotesApi/` | The API — quotes, collections, auth, telemetry |
| `QuotesApi.Tests/` | Unit and integration tests, including a real SQL Server suite via Testcontainers |
| `RefactorOrders/` | The god-method refactor exercise. `Original/OrderController.cs` is the unrefactored file, kept for comparison |
| `hello-cs/`, `hello-ts/` | Day 1 two-language warm-up |

## Prerequisites

- .NET SDK 10
- Docker — required for the SQL Server integration tests in `QuotesApi.Tests/SqlServer/`. Without it those
  tests do not run.

## Getting started

The JWT signing key is deliberately absent from `appsettings.json`. Supply it once, per machine:

```bash
dotnet user-secrets set "Jwt:SigningKey" "$(openssl rand -base64 32)" --project QuotesApi
```

On PowerShell:

```powershell
$bytes = New-Object byte[] 32
([System.Security.Cryptography.RNGCryptoServiceProvider]::new()).GetBytes($bytes)
dotnet user-secrets set "Jwt:SigningKey" ([Convert]::ToBase64String($bytes)) --project QuotesApi
```

The value must be at least 32 bytes — HS256 needs a 256-bit key, and startup rejects anything shorter with a
message naming this command. Each developer generates their own; it is never shared and never committed.

To get an account you can log in with locally, also set a starter user. Both values are optional; without them
the API starts normally and simply has no users, so `POST` and `DELETE` on `/api/quotes` cannot be exercised
until one is created:

```bash
dotnet user-secrets set "Seed:AdminEmail" "you@example.com" --project QuotesApi
dotnet user-secrets set "Seed:AdminPassword" "<choose one>" --project QuotesApi
```

This account is seeded only in the Development environment, and only when both values are present.

Then run:

```bash
dotnet run --project QuotesApi
```

The API listens on `http://localhost:5104` and `https://localhost:7151`. `GET /health` confirms it is up.
EF Core migrations are applied automatically at startup.

User secrets are loaded only in the Development environment. Running locally with
`ASPNETCORE_ENVIRONMENT=Production` will fail to start, which is intended — production reads its secrets from
Azure Key Vault.

## Tests

```bash
dotnet test QuotesApi.Tests/QuotesApi.Tests.csproj
```

The test hosts supply their own synthetic signing key, so no user-secret is needed to run the suite. Start
Docker first if you want the SQL Server tests included.

## Configuration

Configuration resolves in this order, highest first: environment variables, `appsettings.{Environment}.json`,
`appsettings.json`. Typed sections are bound with the options pattern and injected as `IOptions<T>`.

Secrets are never stored in `appsettings.json`. Local development uses user secrets; Azure uses Key Vault
references exposed as environment variables.

## Deployment

Deployed to Azure Container Apps with `azd up`. The infrastructure in `infra/` provisions Application
Insights, a container registry, a Container Apps environment, a user-assigned managed identity, and a Key
Vault.

The signing key is supplied once per environment before the first deploy:

```bash
azd env set AZURE_JWT_SIGNING_KEY "$(openssl rand -base64 32)"
azd up
```

It is written to Key Vault, and the container receives only the vault's URI. At startup the app reads the
secret from the vault using its managed identity, so the key value never appears in the container's
environment, in the Bicep, or in any file in this repository. Use a different key from your local one.

### Known limitation: the deployed database is not durable

The API uses SQLite, and the deployed container writes its database file to the container's own filesystem.
No storage is mounted, so every restart, revision, or scale event starts from an empty database — quotes,
users, and refresh tokens are all lost. The startup migration runs each time and recreates an empty schema.

This is acceptable for the exercise but is not a shape any real deployment should keep. Making it durable
means either mounting Azure Files and pinning the app to a single replica, or moving to Azure SQL and
regenerating the migrations for that provider. Neither is done here.

## Security note

An earlier version of this repository committed the JWT signing key to `appsettings.json`. It has been removed
from the working tree and the key rotated, but the original value remains reachable in Git history and is
treated as compromised. Any token signed with it should be considered untrusted.
