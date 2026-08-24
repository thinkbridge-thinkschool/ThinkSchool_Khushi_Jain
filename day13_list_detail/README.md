# Day 13 piece 2 — quotes list + detail

A second standalone Angular 21 component over the real `QuotesApi`, in its own project so
[piece 1](../day13-signals/) stays as submitted. Adds a `QuotesService` (via `inject()`) and a
detail view on top of piece 1's list. The whole app is [`src/main.ts`](src/main.ts).

## Brief given to the agent

Build a list + detail component against the real API. `GET /api/quotes?page=&size=` returns
`{ page, size, total, items: [{ id, author, text, ownerId, ownerActive }] }`. `GET
/api/quotes/{id}` returns the quote directly — check the controller and entity before assuming it
matches a list item. Signals for loading/error/data on both. `inject()` for the service, real
types, no `any`. Guard against a stale response landing after a newer page or selection.

## Bug caught and fixed

The list request's error handler swallowed the error outright:

```ts
// before
error: () => {
  if (requestId !== this.listRequestId) return;
  this.listStatus.set('error');
},
```

`loadDetail`'s handler right below it takes `err: HttpErrorResponse` and inspects `err.status` —
the list handler took no parameter at all, so a `500` with a trace ID, or a `400` from the real
API's own page/size validation, left no trace anywhere, not even in the console. Fixed by
capturing and logging it, same UI behavior otherwise:

```ts
// after
error: (err: HttpErrorResponse) => {
  if (requestId !== this.listRequestId) return;
  console.error('Failed to load quotes', err);
  this.listStatus.set('error');
},
```

Verified: mocked `/api/quotes` with a `500`, confirmed the UI still shows `Could not read
/api/quotes.` and the browser console now logs `Failed to load quotes HttpErrorResponse` (logged
nothing before the fix). Re-ran the full state/race suite below afterward — unchanged.

I also checked whether reusing one `Quote` interface for list and detail was a mistake I'd made —
`GET /api/quotes/{id}` returns the entity as-is (`isDeleted`, no `ownerActive`), unlike the list
item's projection — but I'd checked `QuoteController.cs`/`Quote.cs` before writing the TypeScript,
so that wrong interface was never actually written.

The race guard is a counter per request kind (`listRequestId`/`detailRequestId`), bumped on every
fetch and checked when the response lands — not a comparison against the current page/selection
value, which gets it wrong if you re-select the same item while an older request for it is still
in flight.

## Verification

Driven headlessly (Playwright + the system's Edge) against the real API through the dev-server
proxy, 16 seeded quotes:

- Normal data: list page 1, click a row → correct detail (`Id 1 / Author Test / Text Hello`).
- Loading: delayed `/api/quotes` 1.2s → `Loading…` shown, then resolves.
- List error: `/api/quotes` → 500 → `Could not read /api/quotes.`.
- Empty list: `/api/quotes` mocked with the real page-beyond-total shape → `No quotes on this page.`.
- Detail error: `/api/quotes/{id}` → 404 → `Quote not found.`.
- List race: delayed page 2 behind page 3, clicked Next twice fast → settles on page 3, unaffected
  once the stale page-2 response lands.
- Detail race: delayed quote 1 behind quote 2, selected 1 then 2 → shows quote 2, unaffected once
  the stale quote-1 response lands.
- No console errors from the app during any of the above.

Not exercised against real data: `ownerActive: false` / a `null` `ownerId` — every seeded quote has
the same active owner, so only a mocked response (matching the real contract) covered that branch.

## What breaks if the API contract changes

Renaming or dropping any field (`items`, `total`, `ownerActive`, `isDeleted`, ...) compiles fine —
nothing validates the response shape at runtime — and just renders as `undefined`. Requiring auth
on either GET turns it into a generic error, not a "sign in" message. A 404 on `/api/quotes/{id}`
changing to some other status falls through to the generic detail error instead of "Quote not
found."

## Run it

```bash
dotnet run --project ../QuotesApi
npm install
npm start
```
