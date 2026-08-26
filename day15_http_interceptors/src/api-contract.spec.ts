/**
 * @vitest-environment node
 *
 * Characterization of the running Week-1 QuotesApi. Every expectation here was
 * read off the real API, not off the C# source. Start it first:
 *
 *   dotnet run --project ../QuotesApi
 *
 * The tests that need an account are skipped without one:
 *
 *   export QUOTES_EMAIL=...      # Seed:AdminEmail
 *   export QUOTES_PASSWORD=...   # Seed:AdminPassword
 */
const BASE = process.env.QUOTES_API ?? 'http://localhost:5104';
const EMAIL = process.env.QUOTES_EMAIL ?? '';
const PASSWORD = process.env.QUOTES_PASSWORD ?? '';

const signIn = () =>
  fetch(`${BASE}/api/auth/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email: EMAIL, password: PASSWORD }),
  });

let TOKEN = '';

beforeAll(async () => {
  if (!EMAIL) {
    return;
  }

  TOKEN = (await (await signIn()).json()).access_token;
});

describe('GET /api/quotes', () => {
  it('returns a page envelope of {page, size, total, items}', async () => {
    const response = await fetch(`${BASE}/api/quotes?page=1&size=3`);

    expect(response.status).toBe(200);
    expect(response.headers.get('content-type')).toContain('application/json');

    const body = await response.json();

    expect(Object.keys(body).sort()).toEqual(['items', 'page', 'size', 'total']);
    expect(body.page).toBe(1);
    expect(body.size).toBe(3);
    expect(typeof body.total).toBe('number');
    expect(Array.isArray(body.items)).toBe(true);
  });

  it('returns items of {id, author, text, ownerId, ownerActive}', async () => {
    const response = await fetch(`${BASE}/api/quotes?page=1&size=3`);
    const body = await response.json();

    expect(body.items.length).toBeGreaterThan(0);

    for (const item of body.items) {
      expect(Object.keys(item).sort()).toEqual(['author', 'id', 'ownerActive', 'ownerId', 'text']);
      expect(typeof item.id).toBe('number');
      expect(typeof item.author).toBe('string');
      expect(typeof item.text).toBe('string');
      expect(typeof item.ownerActive).toBe('boolean');
      expect(item.ownerId === null || typeof item.ownerId === 'string').toBe(true);
    }
  });

  it('answers a page past the end with 200 and an empty items array', async () => {
    const response = await fetch(`${BASE}/api/quotes?page=9999&size=3`);
    const body = await response.json();

    expect(response.status).toBe(200);
    expect(body.items).toEqual([]);
    // total stays the size of the whole collection, not of the page.
    expect(body.total).toBeGreaterThan(0);
  });
});

describe('GET /api/quotes 4xx', () => {
  it('rejects size 0 with a ValidationProblemDetails keyed "page/size"', async () => {
    const response = await fetch(`${BASE}/api/quotes?page=1&size=0`);

    expect(response.status).toBe(400);
    expect(response.headers.get('content-type')).toContain('application/problem+json');

    const body = await response.json();

    expect(body.status).toBe(400);
    expect(body.title).toBe('One or more validation errors occurred.');
    // The key is a literal "page/size", not a field name a form could bind to.
    expect(Object.keys(body.errors)).toEqual(['page/size']);
    expect(body.errors['page/size'][0]).toBe('Page must be >= 1 and size must be between 1 and 100.');
    expect(typeof body.traceId).toBe('string');
    expect(body.detail).toBeUndefined();
  });

  it('rejects size 101 and page 0 the same way', async () => {
    for (const query of ['page=1&size=101', 'page=0&size=5']) {
      const response = await fetch(`${BASE}/api/quotes?${query}`);
      const body = await response.json();

      expect(response.status).toBe(400);
      expect(Object.keys(body.errors)).toEqual(['page/size']);
    }
  });

  it('answers an unknown id with 404 and a zero-length body', async () => {
    const response = await fetch(`${BASE}/api/quotes/999999`);

    expect(response.status).toBe(404);
    expect(response.headers.get('content-length')).toBe('0');
    expect(await response.text()).toBe('');
  });

  it('answers a page it cannot bind with 500 ProblemDetails carrying no errors', async () => {
    const response = await fetch(`${BASE}/api/quotes?page=abc&size=5`);

    expect(response.status).toBe(500);

    const body = await response.json();

    expect(body.title).toBe('An unexpected error occurred.');
    expect(body.errors).toBeUndefined();
  });
});

describe('POST /api/quotes', () => {
  it('answers an unauthenticated create with 401, a zero-length body and a challenge header', async () => {
    const response = await fetch(`${BASE}/api/quotes`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ author: 'a', text: 'b' }),
    });

    expect(response.status).toBe(401);
    expect(response.headers.get('www-authenticate')).toContain('Bearer');
    expect(await response.text()).toBe('');
  });

  it.skipIf(!EMAIL)('creates with 201 and returns {id, author, text, isDeleted, ownerId}', async () => {
    const response = await fetch(`${BASE}/api/quotes`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${TOKEN}` },
      body: JSON.stringify({ author: 'Characterization', text: 'Written by the Day 15 contract test.' }),
    });

    expect(response.status).toBe(201);
    expect(response.headers.get('location')).toMatch(/^\/api\/quotes\/\d+$/);

    const body = await response.json();

    expect(Object.keys(body).sort()).toEqual(['author', 'id', 'isDeleted', 'ownerId', 'text']);
    expect(body.isDeleted).toBe(false);
  });

  it.skipIf(!EMAIL)('rejects an empty author with errors keyed by the PascalCase property name', async () => {
    const response = await fetch(`${BASE}/api/quotes`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${TOKEN}` },
      body: JSON.stringify({ author: '', text: 'b' }),
    });

    expect(response.status).toBe(400);

    const body = await response.json();

    expect(Object.keys(body.errors)).toEqual(['Author']);
    expect(body.errors.Author).toEqual(['The Author field is required.']);
  });

  it.skipIf(!EMAIL)('answers a malformed JSON body with 500, not 400', async () => {
    const response = await fetch(`${BASE}/api/quotes`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${TOKEN}` },
      body: '{not-json',
    });

    expect(response.status).toBe(500);
  });
});

const postJson = (path: string, body: unknown) =>
  fetch(`${BASE}${path}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });

describe('the auth endpoints', () => {
  it('answers wrong credentials with 401 and a zero-length body', async () => {
    const response = await postJson('/api/auth/login', {
      email: 'nobody@example.com',
      password: 'wrong',
    });

    expect(response.status).toBe(401);
    expect(await response.text()).toBe('');
  });

  it('rejects blank credentials with errors keyed Email and Password', async () => {
    const response = await postJson('/api/auth/login', { email: '', password: '' });

    expect(response.status).toBe(400);

    const body = await response.json();

    expect(Object.keys(body.errors).sort()).toEqual(['Email', 'Password']);
  });

  it('takes its refresh token as snake_case refresh_token, and 500s without it', async () => {
    // camelCase misses the JsonPropertyName, leaving the record's property null,
    // which the handler dereferences.
    expect((await postJson('/api/auth/refresh', { refreshToken: 'anything' })).status).toBe(500);
    expect((await postJson('/api/auth/refresh', {})).status).toBe(500);
  });

  it('answers an unknown refresh token with 401', async () => {
    expect((await postJson('/api/auth/refresh', { refresh_token: 'garbage' })).status).toBe(401);
  });

  it('treats logout as idempotent, even for a token it does not know', async () => {
    const response = await postJson('/api/auth/logout', { refresh_token: 'garbage' });

    expect(response.status).toBe(204);
    expect(await response.text()).toBe('');
  });

  it.skipIf(!EMAIL)('grants {access_token, refresh_token, expires_in} on a good login', async () => {
    const body = await (await signIn()).json();

    expect(Object.keys(body).sort()).toEqual(['access_token', 'expires_in', 'refresh_token']);
    expect(body.expires_in).toBe(900);
    expect(body.access_token.split('.')).toHaveLength(3);
  });

  it.skipIf(!EMAIL)('rotates both tokens on refresh', async () => {
    const first = await (await signIn()).json();
    const response = await postJson('/api/auth/refresh', { refresh_token: first.refresh_token });

    expect(response.status).toBe(200);

    const second = await response.json();

    expect(second.refresh_token).not.toBe(first.refresh_token);
    expect(second.access_token).not.toBe(first.access_token);
  });

  it.skipIf(!EMAIL)('revokes the whole family when a spent refresh token is presented again', async () => {
    const first = await (await signIn()).json();
    const second = await (
      await postJson('/api/auth/refresh', { refresh_token: first.refresh_token })
    ).json();

    // Replaying the spent one is what triggers detection...
    expect((await postJson('/api/auth/refresh', { refresh_token: first.refresh_token })).status).toBe(
      401,
    );

    // ...and it takes the legitimate successor down with it, which is why the
    // client must never have two refreshes in flight at once.
    expect((await postJson('/api/auth/refresh', { refresh_token: second.refresh_token })).status).toBe(
      401,
    );
  });
});
