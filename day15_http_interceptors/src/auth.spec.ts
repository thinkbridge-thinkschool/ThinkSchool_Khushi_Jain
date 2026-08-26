import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import {
  ActivatedRoute,
  Router,
  provideRouter,
  type ActivatedRouteSnapshot,
  type RouterStateSnapshot,
  type UrlTree,
} from '@angular/router';
import {
  authInterceptor,
  errorMappingInterceptor,
  retryInterceptor,
  type AccessTokenResponse,
  type AppError,
} from './http';
import { LoginPageComponent } from './login-page';
import { AuthService, SessionStore, authGuard, refreshInterceptor } from './session';

const GRANTED = (suffix: string): AccessTokenResponse => ({
  access_token: `access-${suffix}`,
  refresh_token: `refresh-${suffix}`,
  expires_in: 900,
});

function setup() {
  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(
        withInterceptors([
          errorMappingInterceptor,
          refreshInterceptor,
          authInterceptor,
          retryInterceptor,
        ]),
      ),
      provideHttpClientTesting(),
      provideRouter([]),
    ],
  });

  return {
    http: TestBed.inject(HttpClient),
    backend: TestBed.inject(HttpTestingController),
    session: TestBed.inject(SessionStore),
    auth: TestBed.inject(AuthService),
  };
}

describe('SessionStore', () => {
  it('holds a granted session and gives the transport its bearer token', () => {
    const { session } = setup();

    expect(session.isSignedIn()).toBe(false);

    session.store(GRANTED('1'), 'dev@example.com');

    expect(session.isSignedIn()).toBe(true);
    expect(session.email()).toBe('dev@example.com');
    expect(session.refreshToken()).toBe('refresh-1');
    expect(session.expiresAt()).toBeGreaterThan(Date.now());

    session.clear();

    expect(session.isSignedIn()).toBe(false);
    expect(session.refreshToken()).toBe('');
  });
});

describe('refreshInterceptor', () => {
  it('refreshes on a 401 and replays the request with the new token', () => {
    const { http, backend, session } = setup();
    session.store(GRANTED('1'), 'dev@example.com');
    let created: unknown;

    http.post('/api/quotes', { author: 'a', text: 'b' }).subscribe((quote) => (created = quote));

    const first = backend.expectOne('/api/quotes');
    expect(first.request.headers.get('Authorization')).toBe('Bearer access-1');
    first.flush(null, { status: 401, statusText: 'Unauthorized' });

    const refresh = backend.expectOne('/api/auth/refresh');
    expect(refresh.request.body).toEqual({ refresh_token: 'refresh-1' });
    refresh.flush(GRANTED('2'));

    const replay = backend.expectOne('/api/quotes');
    expect(replay.request.headers.get('Authorization')).toBe('Bearer access-2');
    replay.flush({ id: 7, author: 'a', text: 'b', isDeleted: false, ownerId: 'dev@example.com' });

    expect(created).toMatchObject({ id: 7 });
    expect(session.refreshToken()).toBe('refresh-2');
    backend.verify();
  });

  it('refreshes only once for two requests that 401 together', () => {
    const { http, backend, session } = setup();
    session.store(GRANTED('1'), 'dev@example.com');

    http.post('/api/quotes', { author: 'a', text: 'a' }).subscribe();
    http.post('/api/quotes', { author: 'b', text: 'b' }).subscribe();

    const first = backend.match('/api/quotes');
    expect(first).toHaveLength(2);
    first.forEach((request) => request.flush(null, { status: 401, statusText: 'Unauthorized' }));

    // Two refreshes would present the same rotated token twice, and the API
    // revokes the whole family when it sees that.
    const refreshes = backend.match('/api/auth/refresh');
    expect(refreshes).toHaveLength(1);
    refreshes[0].flush(GRANTED('2'));

    const replays = backend.match('/api/quotes');
    expect(replays).toHaveLength(2);
    replays.forEach((request) => {
      expect(request.request.headers.get('Authorization')).toBe('Bearer access-2');
      request.flush({ id: 1, author: 'a', text: 'a', isDeleted: false, ownerId: null });
    });

    backend.verify();
  });

  it('can refresh again later, after an earlier refresh finished', () => {
    const { http, backend, session } = setup();
    session.store(GRANTED('1'), 'dev@example.com');

    http.post('/api/quotes', {}).subscribe({ next: () => {}, error: () => {} });
    backend.expectOne('/api/quotes').flush(null, { status: 401, statusText: 'Unauthorized' });
    backend.expectOne('/api/auth/refresh').flush(GRANTED('2'));
    backend.expectOne('/api/quotes').flush({ id: 1 });

    http.post('/api/quotes', {}).subscribe({ next: () => {}, error: () => {} });
    backend.expectOne('/api/quotes').flush(null, { status: 401, statusText: 'Unauthorized' });

    const second = backend.expectOne('/api/auth/refresh');
    expect(second.request.body).toEqual({ refresh_token: 'refresh-2' });
    second.flush(GRANTED('3'));
    backend.expectOne('/api/quotes').flush({ id: 2 });

    backend.verify();
  });

  it('clears the session and reports the original 401 when the refresh fails', () => {
    const { http, backend, session } = setup();
    session.store(GRANTED('1'), 'dev@example.com');
    let failure: AppError | undefined;

    http.post('/api/quotes', {}).subscribe({ error: (error: AppError) => (failure = error) });

    backend.expectOne('/api/quotes').flush(null, { status: 401, statusText: 'Unauthorized' });
    backend.expectOne('/api/auth/refresh').flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(failure?.kind).toBe('unauthorized');
    expect(failure?.status).toBe(401);
    expect(session.isSignedIn()).toBe(false);
    expect(session.refreshToken()).toBe('');
    backend.verify();
  });

  it('reports a replayed request\'s own failure, not the 401 that triggered it', () => {
    const { http, backend, session } = setup();
    session.store(GRANTED('1'), 'dev@example.com');
    let failure: AppError | undefined;

    http.post('/api/quotes', {}).subscribe({ error: (error: AppError) => (failure = error) });

    backend.expectOne('/api/quotes').flush(null, { status: 401, statusText: 'Unauthorized' });
    backend.expectOne('/api/auth/refresh').flush(GRANTED('2'));
    backend
      .expectOne('/api/quotes')
      .flush({ title: 'An unexpected error occurred.', status: 500 }, { status: 500, statusText: 'Server Error' });

    expect(failure?.kind).toBe('server');
    backend.verify();
  });

  it('does not try to refresh a 401 from the auth endpoints themselves', () => {
    const { http, backend, session } = setup();
    session.store(GRANTED('1'), 'dev@example.com');
    let failure: AppError | undefined;

    http
      .post('/api/auth/login', { email: 'a', password: 'b' })
      .subscribe({ error: (error: AppError) => (failure = error) });

    backend.expectOne('/api/auth/login').flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(backend.match('/api/auth/refresh')).toHaveLength(0);
    expect(failure?.status).toBe(401);
    backend.verify();
  });

  it('does not try to refresh when there is no refresh token', () => {
    const { http, backend } = setup();
    let failure: AppError | undefined;

    http.post('/api/quotes', {}).subscribe({ error: (error: AppError) => (failure = error) });

    backend.expectOne('/api/quotes').flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(backend.match('/api/auth/refresh')).toHaveLength(0);
    expect(failure?.kind).toBe('unauthorized');
    backend.verify();
  });
});

describe('authGuard', () => {
  const route = {} as ActivatedRouteSnapshot;
  const state = { url: '/quotes' } as RouterStateSnapshot;

  it('lets a signed-in visitor through', () => {
    const { session } = setup();
    session.store(GRANTED('1'), 'dev@example.com');

    expect(TestBed.runInInjectionContext(() => authGuard(route, state))).toBe(true);
  });

  it('sends a signed-out visitor to the login page, remembering where they were going', () => {
    setup();

    const result = TestBed.runInInjectionContext(() => authGuard(route, state)) as UrlTree;

    expect(TestBed.inject(Router).serializeUrl(result)).toBe('/login?returnUrl=%2Fquotes');
  });
});

describe('LoginPageComponent', () => {
  function loginSetup(returnUrl: string | null = null) {
    const navigated: string[] = [];

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(
          withInterceptors([errorMappingInterceptor, refreshInterceptor, authInterceptor]),
        ),
        provideHttpClientTesting(),
        {
          provide: Router,
          useValue: {
            navigateByUrl: (url: string) => {
              navigated.push(url);

              return Promise.resolve(true);
            },
          },
        },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { queryParamMap: { get: () => returnUrl } } },
        },
      ],
    });

    return {
      component: TestBed.createComponent(LoginPageComponent).componentInstance,
      backend: TestBed.inject(HttpTestingController),
      session: TestBed.inject(SessionStore),
      navigated,
    };
  }

  it('signs in and goes to the quotes page', () => {
    const { component, backend, session, navigated } = loginSetup();
    component.email.set('dev@example.com');
    component.password.set('a-password');

    component.submit(new Event('submit'));

    const request = backend.expectOne('/api/auth/login');
    expect(request.request.body).toEqual({ email: 'dev@example.com', password: 'a-password' });
    request.flush(GRANTED('1'));

    expect(session.isSignedIn()).toBe(true);
    expect(component.password()).toBe('');
    expect(navigated).toEqual(['/quotes']);
    backend.verify();
  });

  it('returns to where the guard interrupted', () => {
    const { component, backend, navigated } = loginSetup('/quotes?page=3');

    component.submit(new Event('submit'));
    backend.expectOne('/api/auth/login').flush(GRANTED('1'));

    expect(navigated).toEqual(['/quotes?page=3']);
    backend.verify();
  });

  it('says the credentials were refused on the bodiless 401', () => {
    const { component, backend, session } = loginSetup();

    component.submit(new Event('submit'));
    backend.expectOne('/api/auth/login').flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(component.message()).toBe('That email and password were not accepted.');
    expect(session.isSignedIn()).toBe(false);
    backend.verify();
  });

  it('shows the API\'s own words when it rejects blank fields', () => {
    const { component, backend } = loginSetup();

    component.submit(new Event('submit'));
    backend.expectOne('/api/auth/login').flush(
      {
        title: 'One or more validation errors occurred.',
        status: 400,
        errors: {
          Email: ['The Email field is required.'],
          Password: ['The Password field is required.'],
        },
      },
      { status: 400, statusText: 'Bad Request' },
    );

    expect(component.message()).toBe('The Email field is required. The Password field is required.');
    backend.verify();
  });
});
