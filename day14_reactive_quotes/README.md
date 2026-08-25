# Day 14 piece 1 — create-a-quote form

A standalone Angular 21 component that creates a quote against the real `QuotesApi`, using the
experimental Signal Forms API (`@angular/forms/signals`). One file: [`src/main.ts`](src/main.ts).

## Brief given to the agent

Build a reactive create-a-quote form against the real `POST /api/quotes` contract: validators
matching its actual field limits, clear error messages, associated labels, `aria-invalid` /
`aria-describedby` wiring, keyboard operation, focus moved to the first invalid field on an
invalid submit, and loading/server-error/success states. Find the real endpoint and constraints
first — do not guess field names or limits.

## Bugs caught and fixed

**1. `role="alert"` on a `<ul>` — invalid ARIA (found by `axe-core`).** ARIA-in-HTML doesn't allow
`alert` on `<ul>`, and it strips the list's native role, orphaning its `<li>`s (4 violations).
Fixed by moving `role="alert"` and the `id` onto a wrapping `<div>`, leaving the `<ul>` plain. Also
added a `<main>` landmark, which axe also flagged. 0 violations after, across all three states.

**2. Client `required()` didn't match the server's whitespace rule.** It only rejects a literal
empty string; the server (`Quote.Create`, `IsNullOrWhiteSpace`) also rejects whitespace-only.
`"   "` as Author passed client validation and actually round-tripped to the server before coming
back as a 400. Fixed with a targeted `validate()` alongside `required()`:
```ts
validate(path.author, (ctx) =>
  ctx.value().length > 0 && ctx.value().trim().length === 0
    ? requiredError({ message: 'Author is required.' }) : undefined);
```
Confirmed: no request now leaves the browser for whitespace-only input, and no duplicate message
for a plain empty field.

## Verification

Playwright (system Edge) + `axe-core` against the real API, plus manual in-browser checks for the
token round-trip. All passed: empty-form submit (errors + focus to Author), whitespace/over-length
values rejected client-side, focus-to-first-invalid (Text when only Text is empty), keyboard-only
tab order and Enter-to-submit, `aria-invalid`/`aria-describedby` wired to real elements, axe clean
on pristine/invalid/server-error states, loading state on submit, 401 banner with no/bad token,
successful `201` creation with a valid token. Not exercised: a genuine 500 (code path exists, not
triggered live).

## What breaks if the API contract changes

Renaming `Author`/`Text` — the server would 400 keyed to the new name, which this form's error
mapping wouldn't recognize, so the message would show unattributed instead of on the right field.
Changing the 200/1000 length limits — `maxLength()` here is hardcoded to match today's contract and
would silently desync. Changing the validation error shape away from `ValidationProblemDetails` —
`toSubmitErrors` only knows `err.error.errors` as a `Record<string, string[]>`.

## Run it

```bash
dotnet run --project ../QuotesApi
npm install
npm start
```
