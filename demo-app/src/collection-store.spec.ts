import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { CollectionStore } from './collection-store';
import { errorMappingInterceptor } from './http';

// Every body below was copied from the running API, not written from the C#.
// The two shapes differ on purpose -- see collections-api.ts.
const CREATED_201 = {
  id: 1,
  name: 'Characterisation run',
  ownerId: 'someone@example.com',
  items: [],
};

const details = (items: { quoteId: number }[], itemCount = items.length) => ({
  id: 1,
  name: 'Characterisation run',
  ownerId: 'someone@example.com',
  itemCount,
  items: items.map((item) => ({
    quoteId: item.quoteId,
    author: 'Grace Hopper',
    text: 'A quote',
    addedAt: '2026-08-27T16:17:14.4618892+00:00',
  })),
});

// The API's domain failures carry the message in `detail` with no `errors`
// dictionary, unlike the DataAnnotations failure on create.
const DOMAIN_400 = (detail: string) => ({
  type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
  title: 'Collection validation failed',
  status: 400,
  detail,
  traceId: '00-2c60888d035b6dcd7ddea169f5493fac-ab249644a76dbcc6-01',
});

const CREATE_400 = {
  title: 'One or more validation errors occurred.',
  status: 400,
  errors: {
    Name: ['The field Name must be a string with a minimum length of 3 and a maximum length of 80.'],
  },
};

function setup() {
  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(withInterceptors([errorMappingInterceptor])),
      provideHttpClientTesting(),
    ],
  });

  return {
    store: TestBed.inject(CollectionStore),
    backend: TestBed.inject(HttpTestingController),
  };
}

const read = (backend: HttpTestingController) => backend.expectOne('/api/collections/1');

describe('CollectionStore — loading and empty', () => {
  it('starts idle with nothing open', () => {
    const { store, backend } = setup();

    expect(store.isOpen()).toBe(false);
    expect(store.status()).toBe('idle');
    backend.expectNone(() => true);
  });

  it('is loading while the read model is in flight, then ready', () => {
    const { store, backend } = setup();

    store.open(1);
    expect(store.status()).toBe('loading');

    read(backend).flush(details([{ quoteId: 42 }]));

    expect(store.status()).toBe('ready');
    expect(store.name()).toBe('Characterisation run');
    expect(store.items()).toHaveLength(1);
    backend.verify();
  });

  it('reports an empty collection as empty, not as missing', () => {
    const { store, backend } = setup();

    store.open(1);
    read(backend).flush(details([]));

    expect(store.isEmpty()).toBe(true);
    expect(store.itemCount()).toBe(0);
    expect(store.status()).toBe('ready');
    expect(store.error()).toBe('');
  });

  it('is not empty merely because it has not loaded yet', () => {
    const { store } = setup();

    store.open(1);

    // Still loading: itemCount is 0, but calling that "empty" would flash the
    // empty state on every open.
    expect(store.itemCount()).toBe(0);
    expect(store.isEmpty()).toBe(false);
  });
});

describe('CollectionStore — errors', () => {
  it('names the collection in a 404 rather than showing a blank one', () => {
    const { store, backend } = setup();

    store.open(1);
    read(backend).flush(null, { status: 404, statusText: 'Not Found' });

    expect(store.status()).toBe('error');
    expect(store.error()).toBe('Collection 1 was not found.');
    expect(store.items()).toEqual([]);
  });

  it('surfaces a domain 400 from `detail`, which carries no errors dictionary', () => {
    const { store, backend } = setup();

    store.open(1);
    read(backend).flush(details([{ quoteId: 42 }]));

    store.add(42);
    backend
      .expectOne({ method: 'POST', url: '/api/collections/1/items' })
      .flush(DOMAIN_400('Quote 42 is already in this collection.'), {
        status: 400,
        statusText: 'Bad Request',
      });

    expect(store.lastActionError()).toBe('Quote 42 is already in this collection.');
    // A failed mutation still re-reads: a 400 means the screen and the server
    // have already disagreed once.
    read(backend).flush(details([{ quoteId: 42 }]));
    backend.verify();
  });

  it('surfaces the create 400 from its errors dictionary instead', () => {
    const { store, backend } = setup();

    store.create('ab');
    backend
      .expectOne({ method: 'POST', url: '/api/collections' })
      .flush(CREATE_400, { status: 400, statusText: 'Bad Request' });

    expect(store.lastActionError()).toContain('minimum length of 3');
    expect(store.isOpen()).toBe(false);
    expect(store.isCreating()).toBe(false);
  });

  it('keeps a load error separate from an action error', () => {
    const { store, backend } = setup();

    store.open(1);
    read(backend).flush(details([{ quoteId: 42 }]));

    store.remove(43);
    backend
      .expectOne({ method: 'DELETE', url: '/api/collections/1/items/43' })
      .flush(DOMAIN_400('Quote 43 is not in this collection.'), {
        status: 400,
        statusText: 'Bad Request',
      });
    read(backend).flush(details([{ quoteId: 42 }]));

    expect(store.lastActionError()).toBe('Quote 43 is not in this collection.');
    expect(store.error()).toBe(''); // the collection itself loaded fine
    expect(store.status()).toBe('ready');
  });
});

describe('CollectionStore — a failed re-read after a successful mutation', () => {
  it('keeps the loaded collection on screen and marks it stale', () => {
    const { store, backend } = setup();

    store.open(1);
    read(backend).flush(details([{ quoteId: 42 }]));

    store.add(43);
    backend.expectOne({ method: 'POST', url: '/api/collections/1/items' }).flush(null);

    // The add worked. The re-read that follows it does not.
    read(backend).flush(null, { status: 500, statusText: 'Server Error' });

    // The rows already on screen are out of date, not wrong, so they stay up.
    expect(store.status()).toBe('ready');
    expect(store.items().map((item) => item.quoteId)).toEqual([42]);
    expect(store.isStale()).toBe(true);
    // Nothing the user did failed, so neither error signal is set.
    expect(store.error()).toBe('');
    expect(store.lastActionError()).toBe('');
  });

  it('drops the stale flag as soon as a re-read succeeds', () => {
    const { store, backend } = setup();

    store.open(1);
    read(backend).flush(details([{ quoteId: 42 }]));

    store.add(43);
    backend.expectOne({ method: 'POST', url: '/api/collections/1/items' }).flush(null);
    read(backend).flush(null, { status: 500, statusText: 'Server Error' });
    expect(store.isStale()).toBe(true);

    store.refresh();
    read(backend).flush(details([{ quoteId: 42 }, { quoteId: 43 }]));

    expect(store.isStale()).toBe(false);
    expect(store.items().map((item) => item.quoteId)).toEqual([42, 43]);
  });

  it('still hands the screen to the error when there is nothing loaded to keep', () => {
    const { store, backend } = setup();

    store.open(1);
    read(backend).flush(null, { status: 404, statusText: 'Not Found' });
    expect(store.status()).toBe('error');

    // The picker is rendered whenever a collection is open, error state
    // included, so a mutation can be fired with no details behind it.
    store.add(42);
    backend.expectOne({ method: 'POST', url: '/api/collections/1/items' }).flush(null);
    read(backend).flush(null, { status: 500, statusText: 'Server Error' });

    expect(store.status()).toBe('error');
    expect(store.isStale()).toBe(false);
  });

  it('is cleared by opening another collection', () => {
    const { store, backend } = setup();

    store.open(1);
    read(backend).flush(details([{ quoteId: 42 }]));

    store.add(43);
    backend.expectOne({ method: 'POST', url: '/api/collections/1/items' }).flush(null);
    read(backend).flush(null, { status: 500, statusText: 'Server Error' });
    expect(store.isStale()).toBe(true);

    store.open(1);
    expect(store.isStale()).toBe(false);
    read(backend).flush(details([{ quoteId: 42 }, { quoteId: 43 }]));
  });
});

describe('CollectionStore — itemCount is not items.length', () => {
  it('keeps both counts and names the difference', () => {
    const { store, backend } = setup();

    store.open(1);
    // Exactly what the running API returned after adding quote 999999, which
    // does not exist: counted by the aggregate, dropped by the read model's
    // join to Quotes.
    read(backend).flush(details([{ quoteId: 42 }], 2));

    expect(store.itemCount()).toBe(2);
    expect(store.listedCount()).toBe(1);
    expect(store.unlistedCount()).toBe(1);
  });

  it('never reports a negative gap', () => {
    const { store, backend } = setup();

    store.open(1);
    read(backend).flush(details([{ quoteId: 42 }, { quoteId: 43 }], 1));

    expect(store.unlistedCount()).toBe(0);
  });

  it('counts fullness by the API count, not by what is listed', () => {
    const { store, backend } = setup();

    store.open(1);
    // 50 memberships, only one of them still listable.
    read(backend).flush(details([{ quoteId: 42 }], 50));

    expect(store.isFull()).toBe(true);
  });
});

describe('CollectionStore — concurrent updates', () => {
  it('marks each quote pending independently', () => {
    const { store, backend } = setup();

    store.open(1);
    read(backend).flush(details([]));

    store.add(42);
    store.add(43);

    expect(store.pending().has(42)).toBe(true);
    expect(store.pending().has(43)).toBe(true);
    expect(store.isBusy()).toBe(true);

    backend.match({ method: 'POST', url: '/api/collections/1/items' }).forEach((r) => r.flush(null));
    backend.match('/api/collections/1').forEach((r) => r.flush(details([{ quoteId: 42 }, { quoteId: 43 }])));

    expect(store.isBusy()).toBe(false);
  });

  it('ignores a stale re-read that lands after a newer one', () => {
    const { store, backend } = setup();

    store.open(1);
    read(backend).flush(details([]));

    store.add(42);
    store.add(43);

    const posts = backend.match({ method: 'POST', url: '/api/collections/1/items' });
    expect(posts).toHaveLength(2);

    posts[0].flush(null); // -> re-read A
    posts[1].flush(null); // -> re-read B, newer

    const rereads = backend.match('/api/collections/1');
    expect(rereads).toHaveLength(2);

    // B answers first with the truth, then A arrives late with a stale view
    // that is missing quote 43. Without the generation guard, A would win and
    // the screen would lose an item the server has.
    rereads[1].flush(details([{ quoteId: 42 }, { quoteId: 43 }]));
    rereads[0].flush(details([{ quoteId: 42 }]));

    expect(store.items().map((item) => item.quoteId)).toEqual([42, 43]);
    expect(store.itemCount()).toBe(2);
  });

  it('does not send a second request for a quote already in flight', () => {
    const { store, backend } = setup();

    store.open(1);
    read(backend).flush(details([]));

    store.add(42);
    store.add(42); // a double-click

    expect(backend.match({ method: 'POST', url: '/api/collections/1/items' })).toHaveLength(1);
  });

  it('drops an in-flight re-read when the collection is closed', () => {
    const { store, backend } = setup();

    store.open(1);
    const inFlight = read(backend);

    store.close();
    inFlight.flush(details([{ quoteId: 42 }]));

    expect(store.isOpen()).toBe(false);
    expect(store.status()).toBe('idle');
    expect(store.items()).toEqual([]);
  });
});

describe('CollectionStore — create', () => {
  it('opens the new collection by id and re-reads it as the read model', () => {
    const { store, backend } = setup();

    store.create('Characterisation run');
    backend.expectOne({ method: 'POST', url: '/api/collections' }).flush(CREATED_201);

    // The 201 body is the aggregate shape and has no itemCount, so it is not
    // used as state -- only its id is, and the read model is fetched.
    expect(store.id()).toBe(1);
    read(backend).flush(details([], 0));

    expect(store.status()).toBe('ready');
    expect(store.itemCount()).toBe(0);
    backend.verify();
  });

  it('does not fire a second create while one is in flight', () => {
    const { store, backend } = setup();

    store.create('Characterisation run');
    store.create('Characterisation run');

    expect(backend.match({ method: 'POST', url: '/api/collections' })).toHaveLength(1);
  });
});
