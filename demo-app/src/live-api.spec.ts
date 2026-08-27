import { HttpClient, provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import {
  API_BASE_URL,
  RequestLog,
  authInterceptor,
  errorMappingInterceptor,
  retryInterceptor,
  type AppError,
  type CreatedQuote,
  type QuotesPage,
} from './http';
import { AuthService, SessionStore, refreshInterceptor } from './session';

/**
 * The interceptor chain driven against the running API rather than a mock
 * backend. Start it first: dotnet run --project ../QuotesApi
 *
 * The absolute base URL replaces the dev server's /api proxy, and the fetch
 * backend replaces XHR, because jsdom would block a cross-origin XHR the
 * browser never makes. Everything else is the shipped chain.
 *
 * Signed-in tests need the seeded account:
 *   export QUOTES_EMAIL=...  QUOTES_PASSWORD=...
 */
const BASE = process.env.QUOTES_API ?? 'http://localhost:5104';
const EMAIL = process.env.QUOTES_EMAIL ?? '';
const PASSWORD = process.env.QUOTES_PASSWORD ?? '';

function setup() {
  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(
        withFetch(),
        withInterceptors([
          errorMappingInterceptor,
          refreshInterceptor,
          authInterceptor,
          retryInterceptor,
        ]),
      ),
      provideRouter([]),
      { provide: API_BASE_URL, useValue: BASE },
    ],
  });

  return {
    http: TestBed.inject(HttpClient),
    auth: TestBed.inject(AuthService),
    session: TestBed.inject(SessionStore),
    log: TestBed.inject(RequestLog),
  };
}

async function failure(request: Promise<unknown>): Promise<AppError> {
  try {
    await request;
  } catch (error) {
    return error as AppError;
  }

  throw new Error('expected the request to fail');
}

describe('against the running API', () => {
  it('reads a real page of quotes', async () => {
    const { http } = setup();

    const page = await firstValueFrom(
      http.get<QuotesPage>(`${BASE}/api/quotes`, { params: { page: 1, size: 3 } }),
    );

    expect(page.items.length).toBeGreaterThan(0);
    expect(page.items[0].author.length).toBeGreaterThan(0);
    expect(typeof page.items[0].ownerActive).toBe('boolean');
  });

  it('reads an empty page past the end', async () => {
    const { http } = setup();

    const page = await firstValueFrom(
      http.get<QuotesPage>(`${BASE}/api/quotes`, { params: { page: 9999, size: 3 } }),
    );

    expect(page.items).toEqual([]);
  });

  it('turns the real 400 into a friendly validation error without retrying', async () => {
    const { http, log } = setup();

    const error = await failure(
      firstValueFrom(http.get<QuotesPage>(`${BASE}/api/quotes`, { params: { page: 1, size: 0 } })),
    );

    expect(error.kind).toBe('validation');
    expect(error.status).toBe(400);
    expect(error.message).toBe('Page must be >= 1 and size must be between 1 and 100.');
    expect(error.fieldErrors['page/size']).toHaveLength(1);
    expect(error.traceId).toMatch(/^00-/);
    expect(log.lines()).toHaveLength(0);
  });

  it('retries the real 500 twice, then reports a friendly server error', async () => {
    const { http, log } = setup();

    const started = Date.now();
    const error = await failure(
      firstValueFrom(http.get<QuotesPage>(`${BASE}/api/quotes`, { params: { page: 'abc', size: 5 } })),
    );

    expect(error.kind).toBe('server');
    expect(error.status).toBe(500);
    expect(error.message).toBe('The quotes service is having trouble. Please try again shortly.');
    expect(error.traceId).toMatch(/^00-/);
    expect(log.lines()).toHaveLength(2);
    expect(log.lines()[0]).toContain('retry 1 of 2 in 300ms');
    expect(log.lines()[1]).toContain('retry 2 of 2 in 600ms');
    // 300ms + 600ms of real backoff between the three attempts.
    expect(Date.now() - started).toBeGreaterThanOrEqual(900);
  });

  it('turns the real empty-bodied 401 into a friendly message', async () => {
    const { http } = setup();

    const error = await failure(
      firstValueFrom(http.post(`${BASE}/api/quotes`, { author: 'a', text: 'b' })),
    );

    expect(error.kind).toBe('unauthorized');
    expect(error.status).toBe(401);
    expect(error.message).toBe('You need to be signed in to do that.');
    expect(error.fieldErrors).toEqual({});
  });

  it('reports a real refused login as unauthorized, without trying to refresh', async () => {
    const { auth, session } = setup();

    const error = await failure(
      firstValueFrom(auth.signIn('nobody@example.com', 'definitely-wrong')),
    );

    expect(error.status).toBe(401);
    expect(session.isSignedIn()).toBe(false);
  });

  it.skipIf(!EMAIL)('signs in against the real API and creates a quote with the granted token', async () => {
    const { http, auth, session } = setup();

    await firstValueFrom(auth.signIn(EMAIL, PASSWORD));

    expect(session.isSignedIn()).toBe(true);
    expect(session.email()).toBe(EMAIL);
    expect(session.refreshToken().length).toBeGreaterThan(0);

    const created = await firstValueFrom(
      http.post<CreatedQuote>(`${BASE}/api/quotes`, {
        author: 'Day 15 live test',
        text: 'Created through the interceptor chain.',
      }),
    );

    expect(created.id).toBeGreaterThan(0);
    expect(created.isDeleted).toBe(false);
  });

  it.skipIf(!EMAIL)('rotates both tokens on a real refresh', async () => {
    const { auth, session } = setup();

    const first = await firstValueFrom(auth.signIn(EMAIL, PASSWORD));
    const second = await firstValueFrom(auth.refresh());

    expect(second.access_token).not.toBe(first.access_token);
    expect(second.refresh_token).not.toBe(first.refresh_token);
    expect(session.refreshToken()).toBe(second.refresh_token);
  });

  it.skipIf(!EMAIL)('recovers from a dead access token by refreshing and replaying the POST', async () => {
    const { http, auth, session } = setup();

    await firstValueFrom(auth.signIn(EMAIL, PASSWORD));
    const goodRefreshToken = session.refreshToken();

    // A token the API will reject, with a refresh token it will accept: exactly
    // the state a client is in once its access token has expired.
    session.store(
      { access_token: 'not-a-real-token', refresh_token: goodRefreshToken, expires_in: 900 },
      EMAIL,
    );

    const created = await firstValueFrom(
      http.post<CreatedQuote>(`${BASE}/api/quotes`, {
        author: 'Day 15 live test',
        text: 'Created after a silent refresh.',
      }),
    );

    expect(created.id).toBeGreaterThan(0);
    expect(session.refreshToken()).not.toBe(goodRefreshToken);
    expect(session.isSignedIn()).toBe(true);
  });

  it.skipIf(!EMAIL)('gives up and clears the session when the refresh token is dead too', async () => {
    const { http, auth, session } = setup();

    await firstValueFrom(auth.signIn(EMAIL, PASSWORD));
    session.store({ access_token: 'dead', refresh_token: 'dead-too', expires_in: 900 }, EMAIL);

    const error = await failure(
      firstValueFrom(http.post(`${BASE}/api/quotes`, { author: 'a', text: 'b' })),
    );

    expect(error.kind).toBe('unauthorized');
    expect(session.isSignedIn()).toBe(false);
  });

  it.skipIf(!EMAIL)('does not retry a POST that really fails', async () => {
    const { http, auth, log } = setup();
    await firstValueFrom(auth.signIn(EMAIL, PASSWORD));

    const error = await failure(
      firstValueFrom(
        http.post(`${BASE}/api/quotes`, '{not-json', { headers: { 'Content-Type': 'application/json' } }),
      ),
    );

    expect(error.kind).toBe('server');
    expect(error.status).toBe(500);
    expect(log.lines()).toHaveLength(0);
  });

  it.skipIf(!EMAIL)('rejects an empty author with the API\'s own message, keyed Author', async () => {
    const { http, auth } = setup();
    await firstValueFrom(auth.signIn(EMAIL, PASSWORD));

    const error = await failure(firstValueFrom(http.post(`${BASE}/api/quotes`, { author: '', text: 'b' })));

    expect(error.kind).toBe('validation');
    expect(error.message).toBe('The Author field is required.');
    expect(error.fieldErrors).toEqual({ Author: ['The Author field is required.'] });
  });

  it.skipIf(!EMAIL)('signs out through the real logout endpoint', async () => {
    const { auth, session } = setup();
    await firstValueFrom(auth.signIn(EMAIL, PASSWORD));

    await firstValueFrom(auth.signOut());

    expect(session.isSignedIn()).toBe(false);
    expect(session.refreshToken()).toBe('');
  });
});
