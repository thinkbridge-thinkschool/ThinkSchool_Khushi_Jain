# demo-app

An Angular 21 client for the Quotes API — signals, zoneless, standalone components, over an
ASP.NET Core 10 minimal API.

## Setup, once

```bash
cd demo-app
npm install
cp public/dev-session.example.json public/dev-session.json
```

Put the seeded account in `public/dev-session.json` — the same values as the API's
`Seed:AdminEmail` and `Seed:AdminPassword`. The file is gitignored and never committed, and the
seeding script reads it too, so the credentials never have to be typed onto a command line.

## Run it

Two terminals. The API must be up first, because the dev server proxies `/api` to it.

```bash
dotnet run --project ../QuotesApi       # terminal 1, http://localhost:5104
npm start                                # terminal 2, http://localhost:4217
```

The API seeds a starter account but never seeds quotes, so put the data in yourself.
`seed-quotes.sh` clears what is there and inserts twenty real quotes through the actual endpoint:

```bash
export QUOTES_EMAIL='...'      # Seed:AdminEmail
export QUOTES_PASSWORD='...'   # Seed:AdminPassword
./seed-quotes.sh               # clear, then insert
./seed-quotes.sh --keep        # insert without clearing
```

Clearing goes through `DELETE /api/quotes/{id}`, which the API only allows on quotes you own, so
rows created by another account answer 403 and are reported rather than removed.

## The pages

| Tab | What it does |
|---|---|
| Quotes | Browses quotes with `signal`/`computed`/`effect` and the built-in control flow |
| List & detail | List and detail load independently, with a stale-response guard |
| Create a quote | Adds a quote through Reactive Forms, with full a11y wiring |
| Signal Forms | The same form built on the Signal Forms API |
| HTTP layer | The interceptor chain, with buttons that force each failure |
| Routing | `/routing` and `/routing/:id`, the detail lazy-loaded, with a View Transition between them |
| Collection | One collection's state in signals in a service, over `/api/collections` |

## How it is put together

Four interceptors are global, so every page gets the auth header, a retry on idempotent GETs, a
one-shot refresh-and-replay on a 401, and `ProblemDetails` mapped to a typed `AppError`. Because
`errorMappingInterceptor` is outermost, no page ever receives an `HttpErrorResponse` — every page
reads `AppError`, and the form pages get `fieldErrors` keyed by the API's own `Author` and `Text`
without parsing anything themselves.

Shapes shared across pages (`QuotesPage`, `QuoteSummary`, `QuoteDetail`, `CreatedQuote`) are
declared once in `http.ts`, and the pages share `styles.css` rather than carrying their own
component styles.

The route table is in `routes.ts` rather than `main.ts`, so tests can import it without
bootstrapping the app. Two router features are switched on: `withComponentInputBinding()`, which
lets routed pages take `:id` and `?page=` as inputs instead of subscribing to `paramMap`, and
`withViewTransitions()`, filtered in `main.ts` so it fires only when a navigation both starts and
ends inside `/routing` — every other tab switches instantly.

The Routing tab sits at `/routing` because the Quotes tab already holds `/quotes`. Its detail route
is the only lazily loaded one; `ng build` emits it as its own chunk, and it is fetched on first
navigation to a quote, not before.

`CollectionStore` is `providedIn: 'root'`, so a collection stays open while you visit other tabs.
Every writable signal in it is private and every public member is a readonly signal or a computed,
which makes the store the single writer for that state.

`quotes-api.ts` holds the two read endpoints the Routing and Collection tabs share. The List &
detail tab keeps its own copy of the same two calls.

## Tests

```bash
npx ng test --watch=false
```

The mock-based suites need nothing. The live suites need the API running, and the signed-in ones
skip unless credentials are supplied, which `public/dev-session.json` already has:

```bash
QUOTES_EMAIL=$(grep -o '"email"[^,]*' public/dev-session.json | cut -d'"' -f4) QUOTES_PASSWORD=$(grep -o '"password"[^,]*' public/dev-session.json | cut -d'"' -f4) npx ng test --watch=false
```

The live suites create real quotes through the real endpoint — three of them survive the run. Run
the tests before a demo, not after, or re-run `./seed-quotes.sh` to clear them out.
