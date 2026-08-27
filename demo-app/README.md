# demo-app

The Angular pieces of the programme in one app, over the same Week-1 API.

Days 13 to 15 each stay in their own folder exactly as they were submitted (`day13-signals/`,
`day13_list_detail/`, `day14_reactive_quotes/`, `day14_signal_forms/`, `day15_http_interceptors/`),
and this app was built to demonstrate them together in one place.

From Day 16 on, that changed: a new piece is added here and nowhere else, because this is the app
that keeps growing into a complete one. So this app is now both the demonstration of the earlier
pieces and the home of the newer ones.

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

| Tab | Piece | What it shows |
|---|---|---|
| Quotes | Day 13 piece 1 | `signal`/`computed`/`effect`, built-in control flow, `inject()` |
| List & detail | Day 13 piece 2 | List and detail loading independently, with a stale-response guard |
| Create a quote | Day 14 piece 1 | Reactive Forms with full a11y wiring |
| Signal Forms | Day 14 piece 2 | The same form on the Signal Forms preview API |
| HTTP layer | Day 15 | The interceptor chain, with buttons that force each failure |
| Routing | Day 16 | `/routing` and `/routing/:id`, the detail lazy-loaded, with a View Transition between them |

The Day 16 write-up — the brief, the API contract, the verification log and the bugs — is in
[`DAY16-routing.md`](DAY16-routing.md). The earlier pieces keep their write-ups in their own folders.

## How it signs in

There is no login page in the app. `main.ts` reads `public/dev-session.json`, calls
`POST /api/auth/login` with plain `fetch` before the app is bootstrapped, and seeds the result into
`SessionStore` through an app initializer — which runs before the router's first navigation, so the
guard on the writing pages sees a session that is already in place. From then on `authInterceptor`
attaches the bearer token and `refreshInterceptor` rotates it on a 401.

Every failure in that path returns null instead of throwing. The app still starts, the two
read-only pages still work, and a banner says what to fix.

`/login` still exists, just not in the nav. It is reachable by URL as a fallback if the automatic
sign-in fails or the session is left to expire.

The obvious question about this design: the credentials reach the browser. That is acceptable here
because it is a local development convenience against a local API with a seeded throwaway account,
and the file is not committed. Nothing like it belongs in a deployed build — production has no
seeded account to sign in as, and the file it reads would not exist.

## What changed in the merge

The pages are the submitted ones, with three differences that come from sharing one HTTP stack.

The four interceptors from Day 15 are now global, so every page gets the auth header, the retry on
idempotent GETs, the one-shot refresh-and-replay on a 401, and `ProblemDetails` mapped to a typed
`AppError`. That last one is not optional: `errorMappingInterceptor` means no page ever receives an
`HttpErrorResponse` again, so each ported page reads `AppError`. The two form pages lost their own
`ValidationProblemDetails` parsing, since the interceptor already produces `fieldErrors` keyed by
the API's own `Author` and `Text`.

The dev-only "paste a bearer token" field is gone from both form pages, replaced by the session
above.


The pages share `styles.css` rather than carrying their own component styles, so the app reads as
one app. Shapes shared across pages (`QuotesPage`, `QuoteSummary`, `QuoteDetail`, `CreatedQuote`)
are declared once in `http.ts`.

Only one of the two Day 14 folders held a Reactive Forms implementation. `day14_reactive_quotes/`
was built on Signal Forms despite its name, and the real `ReactiveFormsModule` version was
`day14_signal_forms/src/reactive-quote-for-comparison.ts`, kept unwired as the thing the piece's
comparison was tested against. The Create a quote tab is the port of that file, so both approaches
are present here rather than Signal Forms twice.

## What Day 16 added

The Routing tab is at `/routing`, not `/quotes`, because the Quotes tab already holds that path and
renaming a submitted piece's route to free it up would be the wrong trade.

Two router features are switched on for it. `withComponentInputBinding()` lets the routed pages take
`:id` and `?page=` as inputs rather than subscribing to `paramMap`; no other routed component in
this app declares an input, so nothing else changes. `withViewTransitions()` is filtered in
`main.ts` so it fires only when a navigation both starts and ends inside `/routing` — every other
tab keeps the instant switch it had before.

The guard on that tab is real, but you will not see it fire here: the automatic sign-in above puts a
session in place before the router's first navigation, exactly as it does for the three other
guarded tabs. `day16_routing/` is the standalone version, where the login page is the front door and
the redirect is visible.

`quotes-api.ts` holds the two read endpoints for the Day 16 pages. The List & detail tab keeps its
own copy of the same two calls; deduplicating them would mean editing a submitted piece, which is
not worth it for four lines.

## Tests

The Day 15 suites are carried over unchanged.

```bash
npx ng test --watch=false
```

The mock-based suites need nothing. The live suites need the API running, and the signed-in ones
skip unless credentials are supplied, which `seed-quotes.sh` already has in
`public/dev-session.json`:

```bash
QUOTES_EMAIL=$(grep -o '"email"[^,]*' public/dev-session.json | cut -d'"' -f4) QUOTES_PASSWORD=$(grep -o '"password"[^,]*' public/dev-session.json | cut -d'"' -f4) npx ng test --watch=false
```

The live suites create real quotes through the real endpoint — three of them survive the run. Run
the tests before a demo, not after, or re-run `./seed-quotes.sh` to clear them out.
