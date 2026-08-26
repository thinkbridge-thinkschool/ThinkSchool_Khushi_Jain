import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import {
  API_BASE_URL,
  RequestLog,
  TokenStore,
  authInterceptor,
  errorMappingInterceptor,
  retryInterceptor,
  type AppError,
} from './http';

// Bodies copied from the running API, not written from the C# source.
const VALIDATION_400 = {
  type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
  title: 'One or more validation errors occurred.',
  status: 400,
  errors: { 'page/size': ['Page must be >= 1 and size must be between 1 and 100.'] },
  traceId: '00-2eeb00dd11a13c348a1e03777e661061-f3e54ffab84b2b35-01',
};

const SERVER_500 = {
  type: 'https://tools.ietf.org/html/rfc9110#section-15.6.1',
  title: 'An unexpected error occurred.',
  status: 500,
  traceId: '00-ee62e08973763fc09220479ab4be3794-42cd21ca1fa9860b-01',
};

function setup() {
  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(withInterceptors([errorMappingInterceptor, authInterceptor, retryInterceptor])),
      provideHttpClientTesting(),
    ],
  });

  return {
    http: TestBed.inject(HttpClient),
    backend: TestBed.inject(HttpTestingController),
    tokens: TestBed.inject(TokenStore),
    log: TestBed.inject(RequestLog),
  };
}

function captureError(): { readonly failure: AppError | undefined; observer: { next: () => void; error: (e: AppError) => void } } {
  const captured: { failure: AppError | undefined } = { failure: undefined };

  return {
    get failure() {
      return captured.failure;
    },
    observer: {
      next: () => {},
      error: (e: AppError) => (captured.failure = e),
    },
  };
}

describe('authInterceptor', () => {
  it('sends no Authorization header when no token is stored', () => {
    const { http, backend } = setup();

    http.get('/api/quotes').subscribe();

    const request = backend.expectOne('/api/quotes');
    expect(request.request.headers.has('Authorization')).toBe(false);
    request.flush({ page: 1, size: 5, total: 0, items: [] });
    backend.verify();
  });

  it('sends a bearer header when a token is stored', () => {
    const { http, backend, tokens } = setup();
    tokens.token.set('a-token');

    http.get('/api/quotes').subscribe();

    const request = backend.expectOne('/api/quotes');
    expect(request.request.headers.get('Authorization')).toBe('Bearer a-token');
    request.flush({ page: 1, size: 5, total: 0, items: [] });
    backend.verify();
  });

  it('treats a whitespace-only token as no token', () => {
    const { http, backend, tokens } = setup();
    tokens.token.set('   ');

    http.get('/api/quotes').subscribe();

    const request = backend.expectOne('/api/quotes');
    expect(request.request.headers.has('Authorization')).toBe(false);
    request.flush({ page: 1, size: 5, total: 0, items: [] });
    backend.verify();
  });

  it('does not attach the token to a URL outside this API', () => {
    const { http, backend, tokens } = setup();
    tokens.token.set('a-token');

    http.get('https://example.com/anything').subscribe();

    const request = backend.expectOne('https://example.com/anything');
    expect(request.request.headers.has('Authorization')).toBe(false);
    request.flush({});
    backend.verify();
  });

  it('attaches the token to an absolute API URL when a base URL is configured', () => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([errorMappingInterceptor, authInterceptor, retryInterceptor])),
        provideHttpClientTesting(),
        { provide: API_BASE_URL, useValue: 'http://localhost:5104' },
      ],
    });

    const http = TestBed.inject(HttpClient);
    const backend = TestBed.inject(HttpTestingController);
    TestBed.inject(TokenStore).token.set('a-token');

    http.get('http://localhost:5104/api/quotes').subscribe();

    const request = backend.expectOne('http://localhost:5104/api/quotes');
    expect(request.request.headers.get('Authorization')).toBe('Bearer a-token');
    request.flush({ page: 1, size: 5, total: 0, items: [] });
    backend.verify();
  });

  it('leaves an Authorization header the caller set alone', () => {
    const { http, backend, tokens } = setup();
    tokens.token.set('a-token');

    http.get('/api/quotes', { headers: { Authorization: 'Bearer caller-supplied' } }).subscribe();

    const request = backend.expectOne('/api/quotes');
    expect(request.request.headers.get('Authorization')).toBe('Bearer caller-supplied');
    request.flush({ page: 1, size: 5, total: 0, items: [] });
    backend.verify();
  });
});

describe('retryInterceptor', () => {
  afterEach(() => vi.useRealTimers());

  it('retries a failed GET twice, waiting 300ms then 600ms', async () => {
    vi.useFakeTimers();
    const { http, backend, log } = setup();
    const capture = captureError();

    http.get('/api/quotes').subscribe(capture.observer);

    backend.expectOne('/api/quotes').flush(null, { status: 503, statusText: 'Service Unavailable' });
    backend.expectNone('/api/quotes');

    await vi.advanceTimersByTimeAsync(299);
    backend.expectNone('/api/quotes');

    await vi.advanceTimersByTimeAsync(1);
    backend.expectOne('/api/quotes').flush(null, { status: 503, statusText: 'Service Unavailable' });

    await vi.advanceTimersByTimeAsync(599);
    backend.expectNone('/api/quotes');

    await vi.advanceTimersByTimeAsync(1);
    backend.expectOne('/api/quotes').flush(null, { status: 503, statusText: 'Service Unavailable' });

    expect(capture.failure?.kind).toBe('server');
    expect(log.lines()).toHaveLength(2);
    backend.verify();
  });

  it('delivers the response when a retried GET eventually succeeds', async () => {
    vi.useFakeTimers();
    const { http, backend } = setup();
    let received: unknown;

    http.get('/api/quotes').subscribe((response) => (received = response));

    backend.expectOne('/api/quotes').flush(null, { status: 503, statusText: 'Service Unavailable' });
    await vi.advanceTimersByTimeAsync(300);
    backend.expectOne('/api/quotes').flush({ page: 1, size: 5, total: 1, items: [] });

    expect(received).toEqual({ page: 1, size: 5, total: 1, items: [] });
    backend.verify();
  });

  it('keeps the Authorization header on every retried attempt', async () => {
    vi.useFakeTimers();
    const { http, backend, tokens } = setup();
    tokens.token.set('a-token');

    http.get('/api/quotes').subscribe({ next: () => {}, error: () => {} });

    const first = backend.expectOne('/api/quotes');
    expect(first.request.headers.get('Authorization')).toBe('Bearer a-token');
    first.flush(null, { status: 503, statusText: 'Service Unavailable' });

    await vi.advanceTimersByTimeAsync(300);
    const second = backend.expectOne('/api/quotes');
    expect(second.request.headers.get('Authorization')).toBe('Bearer a-token');
    second.flush({ page: 1, size: 5, total: 0, items: [] });

    backend.verify();
  });

  it('does not retry a 400', async () => {
    vi.useFakeTimers();
    const { http, backend, log } = setup();
    const capture = captureError();

    http.get('/api/quotes').subscribe(capture.observer);

    backend.expectOne('/api/quotes').flush(VALIDATION_400, { status: 400, statusText: 'Bad Request' });

    await vi.advanceTimersByTimeAsync(5000);
    backend.expectNone('/api/quotes');
    expect(capture.failure?.kind).toBe('validation');
    expect(log.lines()).toHaveLength(0);
    backend.verify();
  });

  it('does not retry a 404', async () => {
    vi.useFakeTimers();
    const { http, backend } = setup();
    const capture = captureError();

    http.get('/api/quotes/999999').subscribe(capture.observer);

    backend.expectOne('/api/quotes/999999').flush(null, { status: 404, statusText: 'Not Found' });

    await vi.advanceTimersByTimeAsync(5000);
    backend.expectNone('/api/quotes/999999');
    expect(capture.failure?.kind).toBe('not-found');
    backend.verify();
  });

  it('does not retry a POST, even on a retryable status', async () => {
    vi.useFakeTimers();
    const { http, backend, log } = setup();
    const capture = captureError();

    http.post('/api/quotes', { author: 'a', text: 'b' }).subscribe(capture.observer);

    backend.expectOne('/api/quotes').flush(null, { status: 503, statusText: 'Service Unavailable' });

    await vi.advanceTimersByTimeAsync(5000);
    backend.expectNone('/api/quotes');
    expect(capture.failure?.kind).toBe('server');
    expect(log.lines()).toHaveLength(0);
    backend.verify();
  });

  it('does not retry a DELETE, even on a retryable status', async () => {
    vi.useFakeTimers();
    const { http, backend } = setup();
    const capture = captureError();

    http.delete('/api/quotes/1').subscribe(capture.observer);

    backend.expectOne('/api/quotes/1').flush(null, { status: 502, statusText: 'Bad Gateway' });

    await vi.advanceTimersByTimeAsync(5000);
    backend.expectNone('/api/quotes/1');
    backend.verify();
  });

  it('stops after three attempts rather than retrying forever', async () => {
    vi.useFakeTimers();
    const { http, backend } = setup();
    const capture = captureError();
    let attempts = 0;

    http.get('/api/quotes').subscribe(capture.observer);

    for (let elapsed = 0; elapsed < 10_000; elapsed += 100) {
      for (const request of backend.match('/api/quotes')) {
        attempts++;
        request.flush(null, { status: 503, statusText: 'Service Unavailable' });
      }
      await vi.advanceTimersByTimeAsync(100);
    }

    expect(attempts).toBe(3);
    expect(capture.failure?.status).toBe(503);
    backend.verify();
  });
});

describe('errorMappingInterceptor', () => {
  it('maps the real 400 body to a validation error carrying its field errors', () => {
    const { http, backend } = setup();
    const capture = captureError();

    http.get('/api/quotes').subscribe(capture.observer);
    backend.expectOne('/api/quotes').flush(VALIDATION_400, { status: 400, statusText: 'Bad Request' });

    expect(capture.failure).toMatchObject({
      kind: 'validation',
      status: 400,
      message: 'Page must be >= 1 and size must be between 1 and 100.',
      fieldErrors: { 'page/size': ['Page must be >= 1 and size must be between 1 and 100.'] },
      traceId: VALIDATION_400.traceId,
    });
    backend.verify();
  });

  it('maps the empty-bodied 401 to a friendly unauthorized error', () => {
    const { http, backend } = setup();
    const capture = captureError();

    http.post('/api/quotes', { author: 'a', text: 'b' }).subscribe(capture.observer);
    backend.expectOne('/api/quotes').flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(capture.failure).toMatchObject({
      kind: 'unauthorized',
      status: 401,
      message: 'You need to be signed in to do that.',
      fieldErrors: {},
      traceId: null,
    });
    backend.verify();
  });

  it('maps the empty-bodied 404 to a not-found error', () => {
    const { http, backend } = setup();
    const capture = captureError();

    http.get('/api/quotes/999999').subscribe(capture.observer);
    backend.expectOne('/api/quotes/999999').flush(null, { status: 404, statusText: 'Not Found' });

    expect(capture.failure?.kind).toBe('not-found');
    backend.verify();
  });

  it('keeps the 500 title off the message but keeps its traceId', async () => {
    vi.useFakeTimers();
    const { http, backend } = setup();
    const capture = captureError();

    http.get('/api/quotes').subscribe(capture.observer);

    for (let attempt = 0; attempt < 3; attempt++) {
      backend.expectOne('/api/quotes').flush(SERVER_500, { status: 500, statusText: 'Internal Server Error' });
      await vi.advanceTimersByTimeAsync(1000);
    }

    expect(capture.failure?.kind).toBe('server');
    expect(capture.failure?.message).toBe('The quotes service is having trouble. Please try again shortly.');
    expect(capture.failure?.message).not.toContain('unexpected');
    expect(capture.failure?.traceId).toBe(SERVER_500.traceId);
    vi.useRealTimers();
    backend.verify();
  });

  it('maps a transport failure to a network error', async () => {
    vi.useFakeTimers();
    const { http, backend } = setup();
    const capture = captureError();

    http.get('/api/quotes').subscribe(capture.observer);

    for (let attempt = 0; attempt < 3; attempt++) {
      backend.expectOne('/api/quotes').error(new ProgressEvent('error'));
      await vi.advanceTimersByTimeAsync(1000);
    }

    expect(capture.failure).toMatchObject({
      kind: 'network',
      status: 0,
      message: 'Could not reach the quotes service. Check that it is running, then try again.',
      fieldErrors: {},
    });
    vi.useRealTimers();
    backend.verify();
  });
});
