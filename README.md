# ThinkSchool — Khushi Jain

A quotes API built across the Thinkbridge Day 1–5 programme: ASP.NET Core 10 minimal APIs, EF Core,
self-issued JWT plus Entra ID auth, Serilog, OpenTelemetry, and deployment to Azure Container Apps.

Live: `https://quotes-api.wonderfulplant-5f428237.eastus.azurecontainerapps.io`

## Quick start

Requires .NET SDK 10, plus Docker if you want the SQL Server tests to run.

The JWT signing key is deliberately absent from `appsettings.json`. Set it once per machine:

```bash
dotnet user-secrets set "Jwt:SigningKey" "$(openssl rand -base64 32)" --project QuotesApi
```

```powershell
$b = New-Object byte[] 32
([System.Security.Cryptography.RNGCryptoServiceProvider]::new()).GetBytes($b)
dotnet user-secrets set "Jwt:SigningKey" ([Convert]::ToBase64String($b)) --project QuotesApi
```

There is no registration endpoint, so a starter account is the only way to obtain a token locally. Both
values are optional — without them the API runs but has no users:

```bash
dotnet user-secrets set "Seed:AdminEmail" "you@example.com" --project QuotesApi
dotnet user-secrets set "Seed:AdminPassword" "<choose one>" --project QuotesApi
```

```bash
dotnet run --project QuotesApi
```

Listens on `http://localhost:5104`. `GET /health` confirms it is up; EF Core migrations apply at startup.
User secrets load only in Development — running as Production locally fails by design, since production
reads its secrets from Key Vault.

## Layout

| Path | What it is |
|---|---|
| `QuotesApi/` | The API — quotes, collections, auth, telemetry |
| `QuotesApi.Tests/` | Unit and integration tests, including a real SQL Server suite via Testcontainers |
| `Tests.Domain/` | Aggregate invariant tests. No database, no host, no fixtures |
| `RefactorOrders/` | The god-method refactor exercise. `Original/OrderController.cs` is kept for comparison |
| `hello-cs/`, `hello-ts/` | Day 1 two-language warm-up |
| `infra/` | Bicep for Container Apps, ACR, Key Vault, App Insights |

Write-ups live beside the code they describe: `RefactorOrders/REFACTOR_NOTES.md`,
`RefactorOrders/INITIAL_PROMPT.md`, `RefactorOrders/AI_REFLECTION.md`, `QuotesApi/WHY.md`.

Both `Controllers/` folders use different mechanisms: `QuotesApi/Controllers/` holds minimal-API route
registrations exposed as `Map…Endpoints()` extensions, while `RefactorOrders/Controllers/` is a real
`[ApiController]` deriving from `ControllerBase`.

## Day wise task

One row per task. Paths without a project prefix are relative to `QuotesApi/`.

### Day 1 — foundations

| Task | Where |
|---|---|
| Tools check | submission only |
| Hello in two languages | `hello-cs/Program.cs`, `hello-ts/hello.ts` |
| Minimal API, four quote endpoints | `Controllers/QuoteController.cs`, `Repositories/`, `Migrations/`, `Program.cs` |
| Refactor a god-method controller | `RefactorOrders/` — `Original/OrderController.cs`, `INITIAL_PROMPT.md`, `REFACTOR_NOTES.md`, `Services/`, `Repositories/` |
| AI-assisted strategy refactor | `RefactorOrders/Services/IOrderRule.cs`, `DefaultOrderRules.cs`, `AI_REFLECTION.md` |
| `Collection` aggregate | `Models/Collection.cs`, `CollectionItem.cs`, `Repositories/ICollectionRepository.cs`, `Controllers/CollectionController.cs` |

### Day 2 — depth

| Task | Where |
|---|---|
| DI lifetimes and `IClock` | `Extensions/InfrastructureExtensions.cs`, `Time/IClock.cs`, `Time/SystemClock.cs`, `QuotesApi.Tests/FakeClock.cs` |
| Cancellation through layers | `Controllers/CollectionController.cs`, `Repositories/CollectionRepository.cs`, `QuotesApi.Tests/CollectionRepositoryTests.cs` |
| Domain-layer tests | `Tests.Domain/CollectionInvariantTests.cs` |
| Anemic to rich `Quote` | `Models/Quote.cs`, `Models/QuoteDomainException.cs`, `WHY.md` |
| JWT auth, own issuer | `Controllers/AuthController.cs`, `Services/TokenService.cs`, `Models/User.cs`, `Models/JwtOptions.cs` |
| Refresh rotation, reuse detection | `Controllers/AuthController.cs`, `Services/RefreshTokenEvaluator.cs`, `Models/RefreshToken.cs` |

### Day 3 — auth and tests

| Task | Where |
|---|---|
| Entra ID as a second scheme | `Extensions/InfrastructureExtensions.cs`, `Authorization/AuthenticationSchemeSelector.cs`, `Models/EntraOptions.cs` |
| Authorization policies and claims | `Authorization/CanModifyOwnQuoteHandler.cs`, `CanModifyOwnQuoteRequirement.cs`, `ClaimsPrincipalExtensions.cs` |
| Lock down the API end-to-end | `QuotesApi.Tests/QuoteAuthorizationTests.cs` |
| xUnit with Fluent Assertions | `QuotesApi.Tests/QuoteTests.cs`, `RefreshTokenEvaluatorTests.cs`, `CanModifyOwnQuoteHandlerTests.cs` |
| WebApplicationFactory integration tests | `QuotesApi.Tests/IntegrationTestBase.cs`, `QuotesApiIntegrationTests.cs`, `CollectionApiTests.cs` |
| Real SQL Server via Testcontainers | `QuotesApi.Tests/SqlServer/` |

### Day 4 — CI and observability

| Task | Where |
|---|---|
| CI with GitHub Actions | `.github/workflows/ci.yml`, `ThinkSchool.slnx` |
| Coverage gate | `coverlet.runsettings` |
| Serilog with correlation IDs | `Extensions/ApplicationPipelineExtensions.cs`, `appsettings.json` |
| OpenTelemetry tracing | `Extensions/InfrastructureExtensions.cs` |
| Azure App Insights | `Extensions/InfrastructureExtensions.cs`, `infra/resources.bicep` |
| Configuration and `IOptions` | `Models/JwtOptions.cs`, `Models/EntraOptions.cs`, `Models/SeedOptions.cs` |

### Day 5 — ship it

| Task | Where |
|---|---|
| Diagnose a slow endpoint from traces | `docs/day5-tracing/`, `Controllers/QuoteController.cs` |
| Container image without a Dockerfile | `QuotesApi.csproj` |
| Azure Container Apps | `infra/resources.bicep` |
| Deploy via azd | `azure.yaml`, `infra/main.bicep`, `infra/main.parameters.json` |
| Verify in App Insights with KQL | `docs/day5_KQL/` |
| Polly resilience | `Resilience/EntraHttpClientExtensions.cs`, `QuotesApi.Tests/EntraResilienceHandlerTests.cs` |
| Smoke test and Week 1 reflection | submission only |

## Tests

```bash
dotnet test ThinkSchool.slnx
```

The test hosts supply their own synthetic signing key, so no user secret is needed. Start Docker first to
include the SQL Server suite. The domain suite alone runs in milliseconds:

```bash
dotnet test Tests.Domain/Tests.Domain.csproj
```

## Configuration

Precedence, highest first: environment variables, `appsettings.{Environment}.json`, `appsettings.json`.
Typed sections bind through the options pattern and are injected as `IOptions<T>`.

Secrets never live in `appsettings.json` — user secrets locally, Key Vault in Azure.

## Deployment

```bash
azd env set AZURE_JWT_SIGNING_KEY "$(openssl rand -base64 32)"
azd up
```

The key is written to Key Vault. The container receives only the vault's URI and reads the secret at startup
through its managed identity, so the value never appears in the container's environment, in the Bicep, or in
this repository. Use a different key from your local one.

## Known gaps

- **The deployed database is not durable.** SQLite writes to the container's own filesystem with no volume
  mounted, so every restart, revision, or scale event starts from an empty schema. Fixing it means Azure
  Files with a single replica, or moving to Azure SQL.
- **The original JWT signing key is still in Git history.** It has been removed from the working tree and
  rotated, but the old value remains reachable and is treated as compromised.
- **Two migration sets are maintained by hand.** `QuotesApi/Migrations/` targets SQLite and
  `QuotesApi.Tests/SqlServer/Migrations/` targets SQL Server for the Testcontainers suite. Nothing enforces
  that a new migration lands in both.
