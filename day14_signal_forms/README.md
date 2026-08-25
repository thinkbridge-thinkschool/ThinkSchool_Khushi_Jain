# Day 14 piece 2 — create-a-quote form, Signal Forms

The create-a-quote form rebuilt against the real `POST /api/quotes` using Angular's experimental
Signal Forms (`@angular/forms/signals`): [`signal-quote.ts`](src/signal-quote.ts), bootstrapped by
[`main.ts`](src/main.ts). [`models.ts`](src/models.ts) holds the shared types.

## Wrong assumption caught and fixed

Assumed `maxLength(200)` behaves like a normal post-hoc validator — type past the limit, then get
flagged. It doesn't: `[formField]` also reflects it as a native `maxlength="200"` HTML attribute,
so typing, real pasting, and even Playwright's `fill()` all get silently capped at 200 characters
before the value could ever violate the rule. Verified against
[`reactive-quote-for-comparison.ts`](src/reactive-quote-for-comparison.ts) (kept in this folder,
not part of the shipped app), where the equivalent `Validators.maxLength` *does* fire live, since
it never touches the native attribute. Documented with a comment at the `maxLength()` call.

## Verification

Playwright + `axe-core` against the real API: empty submit shows required errors on both fields
and moves focus to Author (failed submit); whitespace-only author correctly rejected client-side
(needed a custom check alongside `required()`, since neither this nor `Validators.required`
trims); the `maxLength` finding above, confirmed multiple ways; a real successful submission with
a valid token — `POST` returns `201`, button reads "Creating…" while in flight, then "Quote #21
created." (clean submit); a 401 with no token — "Not authorized to create quotes..."; a mocked 500
— "Could not create the quote."; keyboard-only (Tab through bearer-token/Author/Text/button in
order, Enter on the button submits, focus lands on whichever field is actually invalid); `axe`: 0
violations on pristine and invalid states, confirmed both programmatically and with a manual
screen-reader/axe pass — no issues found.

Short comparison to Reactive Forms: `submit()` auto-marks fields touched (Reactive Forms needs an
explicit `markAllAsTouched()`); validator messages attach directly (`field().errors()[i].message`)
instead of needing to map error keys by hand; the `maxLength` behavior above is the one real
rough edge found.

## What breaks if the API contract changes

Renaming `Author`/`Text` — the server would 400 keyed to the new name, which
`toSubmitErrors`' mapping (`key === 'Author' ? field.author : ...`) wouldn't recognize, so the
message would show unattributed instead of on the right field. Changing the 200/1000 length
limits — `maxLength()` here is hardcoded to match today's contract and would silently desync.
Changing the validation error shape away from `ValidationProblemDetails` — `toSubmitErrors` only
knows `err.error.errors` as a `Record<string, string[]>`.

## Run it

```bash
dotnet run --project ../QuotesApi
npm install
npm start
```
