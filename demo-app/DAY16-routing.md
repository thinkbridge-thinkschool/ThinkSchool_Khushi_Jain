# Day 16 — Routing, lazy loading, guards

I directed Claude Code instead of hand-writing this, and verified it myself in a browser.

The piece is the **Routing** tab of this app: [`routes.ts`](src/routes.ts),
[`routed-list-page.ts`](src/routed-list-page.ts), [`routed-detail-page.ts`](src/routed-detail-page.ts),
[`quotes-api.ts`](src/quotes-api.ts) and [`routing.spec.ts`](src/routing.spec.ts). There is no
separate Day 16 application: this app is the one that keeps growing, so each new piece is added to
it rather than standing on its own.

## My brief

Read the repo first. Don't invent endpoints, fields or routes: find the Week-1 Quotes API, read
`Controllers/QuoteController.cs` and `Models/Quote.cs`, and use the list endpoint, the detail
endpoint and the id field it really returns. Reuse the Day 15 HTTP stack and its `authGuard` rather
than inventing a second login.

Then build: a routed quotes list and a routed quote detail, the detail lazy-loaded, a functional
guard that redirects to the existing login page, and a View Transition between the two. Handle a
missing or invalid route parameter, a quote that does not exist, and the API being down — without
ever showing the wrong quote. Don't rewrite the Day 13, 14 or 15 pages already in this app.

Then verify in a real browser, not by assertion: guard pass and guard redirect, the lazy chunk in
the network tab, a bad id. Read the diff like a junior's PR and fix what is wrong.

The contract the brief rests on was not guessed. Day 15 characterized both endpoints with live tests
first — see the table in [`../day15_http_interceptors/README.md`](../day15_http_interceptors/README.md)
and `api-contract.spec.ts` in this app.

## The real API it is built against

Read from [`QuoteController.cs`](../QuotesApi/Controllers/QuoteController.cs) and
[`Quote.cs`](../QuotesApi/Models/Quote.cs), then confirmed with curl against the running API.

| | |
|---|---|
| List | `GET /api/quotes?page=&size=` → `{page, size, total, items:[{id, author, text, ownerId, ownerActive}]}` |
| Detail | `GET /api/quotes/{id:int}` → `{id, author, text, isDeleted, ownerId}` |
| Id field | `id`, an `int`. `Quote.Id` is `int`, and the route is constrained `{id:int}` |
| Missing quote | 404, zero-length body |
| Non-numeric id | 404 as well — no route matches, so it never reaches the handler |

The two endpoints do **not** return the same shape: the list adds `ownerActive` and omits
`isDeleted`, the detail does the opposite. `http.ts` keeps them as two types for that reason.

Both are anonymous — neither carries `RequireAuthorization`. Only `POST` and `DELETE` do. So the
guard below is a gate on the app's navigation, not on the data; see Limitations.

## The routes ([`routes.ts`](src/routes.ts))

```ts
{
  path: 'routing',
  canActivate: [authGuard],
  children: [
    { path: '', component: RoutedListPageComponent },
    {
      path: ':id',
      loadComponent: () =>
        import('./routed-detail-page').then((module) => module.RoutedDetailPageComponent),
    },
  ],
},
```

The guard sits on the parent, so one `canActivate` covers both children and runs *before* the child
is matched — a signed-out visitor never downloads the detail chunk. `login` is a sibling, outside
`routing`, which is what stops the redirect looping.

The pair is at `/routing`, not `/quotes`, because the Quotes tab is Day 13 piece 1 and already owns
that path. Renaming a submitted piece's route to free it up would have been the wrong trade.

## The guard ([`session.ts`](src/session.ts), Day 15's, unchanged)

```ts
export const authGuard: CanActivateFn = (_route, state) => {
  const session = inject(SessionStore);

  if (session.isSignedIn()) {
    return true;
  }

  return inject(Router).createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
};
```

`SessionStore.isSignedIn` reads the access token that a real `POST /api/auth/login` put there.

One thing to be plain about: in this app you will not normally *see* it fire. `main.ts` signs in from
`public/dev-session.json` before the router's first navigation, so the guard passes silently, exactly
as it does for the three other guarded tabs. To watch it work, take that file away:

```bash
mv public/dev-session.json public/dev-session.json.off   # then reload /routing
```

## The detail route ([`routed-detail-page.ts`](src/routed-detail-page.ts))

`withComponentInputBinding()` hands the `:id` segment to the component as an input, so there is no
`paramMap` subscription to leak:

```ts
readonly id = input.required<string>();

private load(rawId: string): void {
  this.inFlight?.unsubscribe();
  const id = parseQuoteId(rawId);

  if (id === null) {
    this.state.set({ kind: 'error', message: `"${rawId}" is not a quote id…`, canRetry: false });
    return;
  }

  this.state.set({ kind: 'loading' });
  this.inFlight = this.quotesApi.getById(id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
    next: (quote) => this.state.set({ kind: 'ready', quote }),
    error: (failure) => { /* 404 names the id; anything else offers a retry */ },
  });
}
```

One `state` signal holds loading, ready and error, so there is no combination of flags that can put
a stale quote on screen next to an error.

## The View Transition

`withViewTransitions()` runs a navigation inside `document.startViewTransition()`. Both templates
derive the same name from the API's id — `[style.view-transition-name]="'quote-' + quote.id"` on the
list card and the same expression on the detail card — so the browser pairs them and morphs one into
the other. Nothing is coordinated between the pages: they agree because they both read `id`.

Because the feature is global and this app has six tabs, it is filtered. `main.ts` flags only
navigations that both start and end inside `/routing`; `styles.css` leaves the animation duration at
`0s` otherwise. Every other tab keeps the instant switch it had before.

## Verification log

`npx ng test --watch=false` — 82 passed, 13 skipped. 26 of those are Day 16's, in
[`routing.spec.ts`](src/routing.spec.ts); the other 56 are the earlier pieces', unchanged. In a real
Chromium, 36 checks across the dev server and a production build, driven with Playwright.

**Lazy loading, production build.** Served `dist/demo-app/browser` with an `/api` proxy and watched
every `.js` request. The chunk `ng build` names `routed-detail-page` is `chunk-4RDKQ3O4.js`, 2.71 kB:

| | requested |
|---|---|
| landing tab (`/quotes`, Day 13) | `chunk-XLKTUCSL.js`, `main.js` |
| after visiting all four other tabs | `chunk-XLKTUCSL.js`, `main.js` — **still no detail chunk** |
| routed list rendered at `/routing` | `chunk-XLKTUCSL.js`, `main.js` — **still none** |
| after clicking a quote | `chunk-XLKTUCSL.js`, `main.js`, **`chunk-4RDKQ3O4.js`** |

Under `ng serve` the same holds, and the request is legible as
`component?c=src/routed-detail-page.ts@RoutedDetailPageComponent` — it names the file.

**Guard, redirect side.** With `dev-session.json` denied so the automatic sign-in fails, `/routing`
→ `/login?returnUrl=%2Frouting` and `/routing/42` → `/login?returnUrl=%2Frouting%2F42`, with no
detail chunk fetched for the blocked navigation. The unguarded Quotes tab still opened.

**Guard, pass side.** Signed in, `/routing` renders 5 quotes from `GET /api/quotes?page=1&size=5`
and `/routing/42` renders quote 42.

**Route parameters.** `/routing/abc` → `"abc" is not a quote id, so there is nothing to look up.`,
and no request goes out for it. `/routing/999999` → one request, 404, `Quote 999999 was not found.`
and no `.detail` block on screen. `/routing/99999999999999999999` rejected client-side. `?page=abc`
is clamped to `page=1` rather than sent to the API, which would have answered 400.

**API failures.** Aborted the detail request in the browser: the banner reads *Could not reach the
quotes service…*, it appears 1373 ms in — Day 15's retry interceptor doing 300 ms + 600 ms of
backoff first — a **Try again** button is offered, and no stale quote is left on screen. Restoring
the API and clicking it loads the quote. A forced 500 shows *The quotes service is having trouble*,
not the problem document's title.

**View Transition.** Forced the animation to 1500 ms and measured `transition.finished`: 1558 ms
list→detail and 1563 ms detail→list, against 66 ms and 48 ms for a tab switch — so the filter works.
A screenshot 500 ms in shows the list card for quote 42 mid-morph into the detail card.

**The earlier pieces still work.** All five existing tabs render, each asserted by its own component
selector at its own URL, and the test count for them is unchanged at 56.

## Two bugs I made it fix

**The list cancelled nothing between pages.** It had `takeUntilDestroyed(destroyRef)` on the request
and a comment claiming that covered a superseded page — it does not; `DestroyRef` only fires when the
component is destroyed, and paging reuses the same instance. Two quick clicks of **Next** could
therefore land page 2's response after page 3's and leave page 3 in the URL showing page 2's quotes.
Fixed by aborting the in-flight request before starting the next, and pinned by *"abandons the
request for the page that was left"*, which asserts `first.cancelled`.

**Filtering the transition with `skipTransition()` broke the console.** Skipping rejects
`transition.ready`, which the router itself passes to `console.error` in dev mode — so every
navigation outside `/routing` logged `AbortError: Transition was skipped`. The browser run showed 13
console errors in an app that previously had none. Replaced with a `routed-transition` class on
`<html>` that gates the animation in CSS, ref-counted so an overlapping navigation cannot have its
class stripped by the previous transition settling. Back to zero.

## The assumption the API corrected

`GET /api/quotes/abc` answers **404, not 400** — the route is `group.MapGet("/{id:int}", …)`, so a
non-numeric segment matches no route at all and never reaches the handler. Angular's `:id` matches
anything, so `/routing/abc` does reach the component. Had it been passed through, the user would have
read *"Quote abc was not found"* — which implies it might have existed. `parseQuoteId` rejects it
before the request instead.

## What breaks if the detail route or the id field changes

- **The route is renamed.** `getById` builds `/api/quotes/${id}` in [`quotes-api.ts`](src/quotes-api.ts)
  and would start getting 404s. The app would say *"Quote 42 was not found"* for a quote that exists,
  because a 404 for a missing row and a 404 for a missing route are the same status and nothing
  distinguishes them.
- **`id` is renamed.** The list still renders — `text` and `author` are untouched — but
  `[routerLink]="['/routing', quote.id]"` builds `/routing/undefined`, `parseQuoteId` rejects it, and
  every quote opens as *"is not a quote id"*. It would read as a routing bug, not a contract change.
- **`id` stops being an int** (a GUID, say). `parseQuoteId`'s `/^\d+$/` would reject every real id,
  the same way. The whole assumption lives in that one function, which is where to change it.
- **Either one changes.** The View Transition name is derived from the id, so it stops pairing and
  quietly degrades to the root crossfade — no error, just a lost animation.

## Limitations

- **The guard protects navigation, not data.** `GET /api/quotes` and `GET /api/quotes/{id}` are
  anonymous on the API, so anyone can still curl them. Making the guard mean something at the data
  layer is an API change, not an Angular one, and is out of scope here.
- The guard is invisible in normal use because of this app's automatic sign-in; the recipe above is
  how to see it.
- Everything above was checked in Chromium. Firefox has no `document.startViewTransition`; the
  router falls back to a plain navigation there, which I did not run.
