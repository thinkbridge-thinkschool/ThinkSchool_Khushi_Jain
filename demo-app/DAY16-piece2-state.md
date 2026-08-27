# Day 16 piece 2 — State management, signals first

One collection's state, in signals, in a service, against my real `/api/collections` endpoints.
I directed Claude Code and verified it against the running API myself.

Files: [`collection-store.ts`](src/collection-store.ts), [`collections-api.ts`](src/collections-api.ts),
[`collection-page.ts`](src/collection-page.ts), [`collection-store.spec.ts`](src/collection-store.spec.ts).
It is the Collection tab of this app.

I picked collections because it was the one part of my Week-1 API no Angular piece had touched, so
nothing could be copied from Days 13–16 piece 1.

## 1. The brief I gave the agent

- Model one collection's state with signals in a service, against the real `/api/collections`.
- Characterise the endpoints with curl first. Write the store against what they return, not what the
  C# suggests.
- Handle loading, error, empty and concurrent add/remove.
- Keep the component a reader: every writable signal private, every public member a readonly signal
  or a computed.
- Draft the rule for when I'd move to a signal store or NgRx. I'll rewrite it in my own words.

## 2. The real API

Characterised with curl before any code was written.

| Request | What actually comes back |
|---|---|
| `POST /api/collections` | 201 `{id, name, ownerId, items:[]}` — the aggregate. No `itemCount`. Needs `can-edit-quotes` |
| `GET /api/collections/{id}` | 200 `{id, name, ownerId, itemCount, items:[{quoteId, author, text, addedAt}]}` — the read model. Anonymous |
| `POST /api/collections/{id}/items` | 204. Body `{quoteId}` |
| `DELETE /api/collections/{id}/items/{quoteId}` | 204 |
| duplicate add | 400 `{detail:"Quote 42 is already in this collection."}` — no `errors` dictionary |
| remove a non-member | 400, `detail:"Quote 43 is not in this collection."` |
| `POST /api/collections` name `"ab"` | 400 **with** `errors:{Name:[…]}` — a different 400 shape |
| unknown collection | 404. Mutations without a token | 401 |

Two things guessing would have got wrong. The 201 and the 200 are different shapes, so the store
throws the 201 away and keeps only its `id`. And `itemCount` is not `items.length`: adding a quote
that does not exist returns 204, and `CollectionDetailsQuery` joins to `Quotes` with
`where !quote.IsDeleted`, so a membership can be counted and not listed. The store keeps both counts
and the page names the gap rather than hiding it.

## 3. The store

Two rules hold it together.

**Every writable signal is private; every public member is readonly or a computed.** A component can
read state and call a method, never assign to it. That is what makes the store the single writer.

```ts
private readonly details = signal<CollectionDetails | null>(null);

readonly items = computed(() => this.details()?.items ?? []);
readonly itemCount = computed(() => this.details()?.itemCount ?? 0);   // the API's
readonly listedCount = computed(() => this.items().length);            // what it returned
readonly isEmpty = computed(() => this.loadStatus() === 'ready' && this.itemCount() === 0);
```

`isEmpty` requires `status === 'ready'`, or every open flashes the empty state while nothing has
loaded yet.

**The server is the only source of truth about membership.** All three mutations answer 204 with no
body, so there is nothing to merge — every one is followed by a re-read, on failure as well as
success. Concurrency is a generation counter, not a library:

```ts
const generation = ++this.refreshGeneration;
// ...
if (generation !== this.refreshGeneration) return;   // a newer re-read is on its way
```

Three adds in flight mean three re-reads and only the newest may be applied. `add()` also refuses a
quote already pending, so a double-click never leaves the browser.

## 4. The rule: when I'd move to a signal store, and when to NgRx

| Step | Move here when |
|---|---|
| Signals in the component | State is one screen's and dies with it |
| **Signals in a service — where this sits** | A second component reads the same state, or it must outlive a route change |
| A signal store (NgRx SignalStore) | The same slice has more than one writer and needs shared plumbing — entities keyed by id, a dozen-plus selectors. You catch yourself hand-rolling `withEntities` |
| Full NgRx | You need a replayable record of *why* state changed, effects that outlive every component, or many features writing one slice |

`CollectionStore` has one writer, about ten derived values and one consumer, so I am staying at step
two. The one hard problem it has — interleaved re-reads — is a generation counter, and NgRx would
not have prevented it, because the race is between HTTP responses, not reducers.

## 5. Verification log

`npx ng test --watch=false` — 103 passed, 13 skipped; 21 are this piece's, and every response body
in them was copied from the running API. Plus 20 checks in a real Chromium against the live API.

**Loading.** `open(1)` is `loading` before any response, `ready` after. A mutation's re-read does not
set `loading`, so the list does not flicker on every add; only the Refresh button does.

**Empty.** A new collection shows "Nothing in this collection yet", `itemCount` 0, no error. Tested
separately that a loading store is not reported as empty.

**Error.** `GET /api/collections/999999` → 404 → "Collection 999999 was not found." A duplicate add →
the API's own `detail` text from a 400 with no `errors` dictionary. Removing a non-member → "Quote 43
is not in this collection."

**Concurrent updates.** Three adds in one tick → exactly 3 POSTs, and the screen converged on the
API's count within 200–400 ms, matching a live `fetch` made from inside the page. A unit test covers
two re-reads racing, the newer answering first, and asserts the stale one is dropped.

### The bug I caught in the agent's diff, and made it fix

I read the store the way I'd read a junior's PR and pushed on the mutation path, since it is the only
place two requests happen for one click.

`settle()` re-reads after every mutation. The agent had written that re-read deliberately *not* to
set `status` to `'loading'`, so the list would not flicker — but its failure branch was the same code
the initial load used: set `loadError`, set `status` to `'error'`. The page switches on `status()`,
and its `'error'` case renders a banner and nothing else.

So the re-read was background when it succeeded and foreground when it failed. Concretely: three
quotes on screen, I click add, the POST returns 204, the follow-up GET hits a 500. The add worked and
the rows are still real, but the screen throws them away and shows an error for a request I never
made. It also contradicted the agent's own note that a failed add does not blank a collection that
loaded fine — true of the POST, not of the re-read behind it.

The fix: `reread()` takes a mode. `open()` and `refresh()` are foreground, so their errors still own
the screen. The one inside `settle()` is background:

```ts
if (mode === 'background' && this.details() !== null) {
  this.staleView.set(true);

  return;
}
```

`isStale` is a third signal rather than part of `lastActionError`, because "your add failed" and
"your add worked but I can't confirm the collection now" want different words on screen — and folding
them would have let the re-read overwrite the API's own `detail` text. Four tests pin it; the first
fails against the code as the agent wrote it.

### What breaks if the API contract changes

- **`itemCount` starts meaning `items.length`.** The unlisted-membership banner never shows again.
  Nothing looks broken — the app just stops reporting a real condition. The one I'd least likely
  notice.
- **`items[].quoteId` is renamed.** `track` and `memberQuoteIds()` collapse to `undefined`, the
  picker stops marking anything as added, and every add re-POSTs into a 400. It would look like a
  double-click bug, not a contract change.
- **A mutation starts returning a body instead of 204.** Nothing breaks — the store ignores mutation
  responses and re-reads. That is the one change it is immune to, by design.
