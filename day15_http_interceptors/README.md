# Day 15 — HttpClient + functional interceptors

I directed Claude Code instead of hand-writing this, and verified it myself.

## My brief

Characterize the real API first and prove it with a test before any UI exists. Then wire `HttpClient`
with functional interceptors against that contract: bearer auth header, retry-with-backoff on
idempotent GETs only, `ProblemDetails` mapped to a typed error with a friendly message. New folder,
don't touch Day 13 or 14. Then read the diff like a junior's PR and find a real bug.

## The real API ([`api-contract.spec.ts`](src/api-contract.spec.ts))

| Request | What actually comes back |
|---|---|
| `GET /api/quotes?page=1&size=3` | 200 `{page, size, total, items:[{id, author, text, ownerId, ownerActive}]}` |
| `GET /api/quotes?page=9999&size=3` | 200, `items: []`, `total` still the whole collection |
| `GET /api/quotes?page=1&size=0` | 400 problem+json, `errors` keyed `"page/size"` |
| `GET /api/quotes/999999` | 404, zero-length body |
| `GET /api/quotes?page=abc` | 500 `ProblemDetails`, no `errors` |
| `POST /api/quotes` no token | 401, zero-length body |
| `POST /api/quotes` valid + token | 201 `{id, author, text, isDeleted, ownerId}` |
| `POST /api/quotes` empty author | 400, `errors` keyed `Author` (PascalCase) |
| `POST /api/auth/login` | 200 `{access_token, refresh_token, expires_in: 900}`; wrong password 401 empty |
| `POST /api/auth/refresh` | takes snake_case `refresh_token`, rotates both tokens; a replayed one 401s and revokes the family |

Guessing would have got three things wrong: a 4xx body may not exist at all, the list endpoint's key
is a literal `page/size`, and refresh takes `refresh_token`, not `refreshToken`.

## What it built

- `authInterceptor` — bearer header when the session has a token, this API's URLs only.
- `retryInterceptor` — `GET`/`HEAD` only, twice, 300ms then 600ms, statuses 0/408/429/500/502/503/504.
- `refreshInterceptor` — one 401 becomes one refresh and one replay, with at most one refresh in
  flight, because the API revokes the token family if a spent refresh token is presented twice.
- `errorMappingInterceptor` — any failure to an `AppError` (`kind`, friendly `message`, `fieldErrors`,
  `traceId`), outermost so it maps only what survives.

A login route, a route guard and sign-out sit on top of that. Only `message` reaches the page, and the
token stays in memory, so a reload signs you out.

## Verification

69 tests green with the API running (`npx ng test --watch=false`) and a clean `ng build`. Against the
real API: the 400 and its friendly message, the 500 retried three times with ~900ms of real backoff, a
POST 500 not retried, a real sign-in and create, a real rotation, and a dead access token silently
refreshed and the POST replayed. The rendered pages are a manual check.

## Two bugs I made it fix

A retried GET could land after a newer page and replace it with an error, because the retry
interceptor holds a failure for up to 900ms. Reproduced as a failing test first, fixed by cancelling
the superseded request.

The auth interceptor matched the API with `url.startsWith('/api/')`, so the header silently vanished
for absolute URLs. Every mock test passed; three live tests failed. Fixed with an `API_BASE_URL` token
that the service and the interceptor both read.

## Run it

```bash
dotnet run --project ../QuotesApi
npm install
npm start          # http://localhost:4216
```

```bash
export QUOTES_EMAIL=...      # Seed:AdminEmail
export QUOTES_PASSWORD=...   # Seed:AdminPassword
npm test                     # without these, the signed-in tests skip
```
