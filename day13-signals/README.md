# Day 13 — Signals, zoneless, standalone

One standalone Angular 21 component reading my Week-1 quotes API. The whole app is
[`src/main.ts`](src/main.ts). No NgModule, no zone.js.

## The brief given to the agent

> Build a standalone Angular 21 component against my real Week-1 API, `QuotesApi`.
>
> `GET /api/quotes?page={n}&size={n}` needs no token and answers with an envelope, not a bare array:
>
> ```json
> { "page": 1, "size": 5, "total": 16,
>   "items": [{ "id": 1, "author": "Test", "text": "Hello",
>               "ownerId": "admin@example.com", "ownerActive": true }] }
> ```
>
> Use those field names exactly. State is `signal()`, derived state is `computed()`, the fetch is
> driven by `effect()`. Derive one computed from two signals: the fetched page and an author filter
> box. Render the list with `@for` and a `track`, switch the load state with `@switch`, branch the
> empty list with `@if`. Use `inject()`. One file, no NgModule.

## Agent output

The agent generated the whole app as one standalone component in [`src/main.ts`](src/main.ts), plus the
config to run it (`angular.json`, `tsconfig.json`, `package.json`, `proxy.conf.json`). That file is the
output, with the one fix from [The bug](#the-bug) applied. The parts the exercise asks about:

```ts
readonly page = signal(1);
readonly author = signal('');

// one computed over two signals
readonly visible = computed(() => {
  const needle = this.author().trim().toLowerCase();
  return this.quotes().filter((quote) => quote.author.toLowerCase().includes(needle));
});

private readonly http = inject(HttpClient);

constructor() {
  effect(() => this.load(this.page()));
}
```

```html
@switch (status()) {
  @case ('loading') { <p>Loading…</p> }
  @case ('error') { <p>Could not read /api/quotes.</p> }
  @default {
    @if (visible().length === 0) {
      <p>Nothing on this page matches.</p>
    } @else {
      @for (quote of visible(); track quote.id) { <li>{{ quote.text }} — {{ quote.author }}</li> }
    }
  }
}
```

The effect reads only `page`, so typing in the filter box recomputes `visible` without refetching. The
app has no zone.js; it relies on Angular's signal-based change detection.

## Verification log

Headless Edge against the API on `localhost:5104`, 16 quotes, page size 5.

| What I exercised | Result |
|---|---|
| First load — `@for`, `inject(HttpClient)` | `5 shown of 16 total`, rows `Hello — Test`, `Quote number 1 — Author 1` … |
| Computed on filter `Author 2` | `1 shown of 16 total`, one row `Quote number 2 — Author 2` |
| `track quote.id` | Rows tagged `data-probe` before filtering; the survivor kept `data-probe="p2"`, so the node was moved, not rebuilt |
| Empty list — `@if` on filter `zzz` | `0 shown of 16 total`, `Nothing on this page matches.`, no rows |
| Pagination — Next, `page` signal + effect | `page 2`, rows become quotes 5–9 — the effect refetched |
| Last page | `page 4`, one row, Next `disabled` |
| Error state — `@switch`, API stopped | `Could not read /api/quotes.` |
| Loading state — `@switch`, response held 2s | `Loading…` |
| Zoneless | `typeof window.Zone` is `undefined` |
| Standalone | `bootstrapApplication` boots it; no `NgModule` or `zone.js` anywhere in `src/`, `package.json`, `angular.json` |

By `curl`: `?page=9&size=5` gives `200` with `"items":[]`; `?size=0` gives `400`.

## The bug

Clicking Next twice leaves two requests in flight and the slower one wins. Holding the page-2 response
for 2.5s while page 3 answered reproduced it — the pager read `page 3`, the list showed quotes 5–9. An
effect starts a request but cancels nothing, so stale responses are now dropped:

```ts
if (this.page() !== page) {
  return;
}
```

## What would break if the API contract changed

The component assumes the current response shape. If `items`, `total` or `ownerActive` were renamed, or
the envelope became a bare array, TypeScript would still compile and the page would quietly render an
empty list instead of an error.

## Run it

The API has no CORS policy, so the dev server proxies `/api` ([`proxy.conf.json`](proxy.conf.json)).

```bash
dotnet run --project ../QuotesApi
npm install
npm start
```
