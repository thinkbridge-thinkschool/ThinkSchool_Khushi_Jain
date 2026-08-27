import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { TokenStore, authInterceptor, errorMappingInterceptor, retryInterceptor } from './http';
import { parseQuoteId } from './quotes-api';
import { routes } from './routes';
import { refreshInterceptor } from './session';
import { SignalsPageComponent } from './signals-page';

// Bodies as the running API returns them, carried over from Day 15's
// characterization in api-contract.spec.ts.
const PAGE = {
  page: 1,
  size: 5,
  total: 21,
  items: [
    { id: 42, author: 'Grace Hopper', text: 'A quote', ownerId: 'someone', ownerActive: true },
  ],
};

const QUOTE_42 = {
  id: 42,
  author: 'Grace Hopper',
  text: 'A quote',
  isDeleted: false,
  ownerId: 'someone',
};

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
      provideRouter(routes, withComponentInputBinding()),
    ],
  });

  return {
    backend: TestBed.inject(HttpTestingController),
    router: TestBed.inject(Router),
    tokens: TestBed.inject(TokenStore),
  };
}

// The guard reads SessionStore, which reads TokenStore. Signing in for a test
// means putting a token where the real sign-in would have put it. The app's own
// automatic sign-in does not run here, so the signed-out cases are reachable --
// which they are not in the browser.
function signIn(tokens: TokenStore) {
  tokens.token.set('a-test-access-token');
}

const text = (harness: RouterTestingHarness, selector: string) =>
  harness.routeNativeElement?.querySelector(selector)?.textContent?.trim() ?? '';

describe('the Day 16 route table', () => {
  it('lazy-loads the detail route and nothing else', () => {
    const routing = routes.find((route) => route.path === 'routing');
    const children = routing?.children ?? [];
    const detail = children.find((route) => route.path === ':id');
    const list = children.find((route) => route.path === '');

    expect(detail?.loadComponent).toBeDefined();
    // A `component` here would mean the class was imported statically, which is
    // exactly the mistake the lazy chunk is meant to avoid.
    expect(detail?.component).toBeUndefined();
    expect(list?.component).toBeDefined();
  });

  it('guards the list and the detail with one canActivate on their parent', () => {
    const routing = routes.find((route) => route.path === 'routing');

    expect(routing?.canActivate).toHaveLength(1);
    expect(routing?.children?.every((child) => child.canActivate === undefined)).toBe(true);
  });

  it('leaves the login route unguarded, so the redirect cannot loop', () => {
    expect(routes.find((route) => route.path === 'login')?.canActivate).toBeUndefined();
  });

  it('does not collide with the paths the earlier pieces already own', () => {
    const paths = routes.map((route) => route.path);

    expect(new Set(paths).size).toBe(paths.length);
    // The Quotes tab is Day 13 piece 1 and keeps /quotes, which is why the
    // Day 16 pair lives under /routing. Compared by identity, not by class
    // name -- a production build renames the class.
    expect(routes.find((route) => route.path === 'quotes')?.component).toBe(SignalsPageComponent);
  });
});

describe('authGuard on the routed pair', () => {
  it('sends a signed-out visitor to /login with the route they wanted', async () => {
    const { router, backend } = setup();

    await router.navigateByUrl('/routing/42');

    expect(router.url).toBe('/login?returnUrl=%2Frouting%2F42');
    backend.expectNone(() => true);
  });

  it('lets a signed-in visitor through to the detail route', async () => {
    const { router, tokens } = setup();
    signIn(tokens);

    const activated = await router.navigateByUrl('/routing/42');

    expect(activated).toBe(true);
    expect(router.url).toBe('/routing/42');
  });

  it('guards the routed list as well as the routed detail', async () => {
    const { router } = setup();

    await router.navigateByUrl('/routing');

    expect(router.url).toBe('/login?returnUrl=%2Frouting');
  });

  it('leaves the unguarded Quotes tab reachable when signed out', async () => {
    const { router } = setup();

    await router.navigateByUrl('/quotes');

    expect(router.url).toBe('/quotes');
  });
});

describe('parseQuoteId', () => {
  it('accepts what the API can answer for', () => {
    expect(parseQuoteId('42')).toBe(42);
  });

  it.each(['abc', '', null, undefined, '4.2', '-1', '0', ' 42', '4 2', '99999999999999999999'])(
    'rejects %o',
    (value) => {
      expect(parseQuoteId(value)).toBeNull();
    },
  );
});

describe('the routed detail page', () => {
  it('asks the Week-1 endpoint for the id in the route parameter', async () => {
    const { tokens, backend } = setup();
    signIn(tokens);

    const harness = await RouterTestingHarness.create('/routing/42');
    backend.expectOne('/api/quotes/42').flush(QUOTE_42);
    harness.detectChanges();

    expect(text(harness, '.detail dd')).toBe('42');
    backend.verify();
  });

  it('refetches when only the route parameter changes', async () => {
    const { tokens, backend } = setup();
    signIn(tokens);

    const harness = await RouterTestingHarness.create('/routing/42');
    backend.expectOne('/api/quotes/42').flush(QUOTE_42);

    await harness.navigateByUrl('/routing/43');
    backend.expectOne('/api/quotes/43').flush({ ...QUOTE_42, id: 43 });
    harness.detectChanges();

    expect(text(harness, '.detail dd')).toBe('43');
    backend.verify();
  });

  it('names the 404 rather than showing an empty quote', async () => {
    const { tokens, backend } = setup();
    signIn(tokens);

    const harness = await RouterTestingHarness.create('/routing/999999');
    // The API answers a missing quote with a 404 and a zero-length body.
    backend.expectOne('/api/quotes/999999').flush(null, { status: 404, statusText: 'Not Found' });
    harness.detectChanges();

    expect(text(harness, '.banner.bad')).toBe('Quote 999999 was not found.');
    expect(harness.routeNativeElement?.querySelector('.detail')).toBeNull();
    backend.verify();
  });

  it('does not call the API for a parameter the API could not match', async () => {
    const { tokens, backend } = setup();
    signIn(tokens);

    const harness = await RouterTestingHarness.create('/routing/abc');
    harness.detectChanges();

    expect(text(harness, '.banner.bad')).toContain('is not a quote id');
    backend.expectNone(() => true);
  });
});

describe('the routed list page', () => {
  it('reads the page from the query string', async () => {
    const { tokens, backend } = setup();
    signIn(tokens);

    const harness = await RouterTestingHarness.create('/routing?page=3');
    const request = backend.expectOne((r) => r.url === '/api/quotes');

    expect(request.request.params.get('page')).toBe('3');
    request.flush({ ...PAGE, page: 3 });
    harness.detectChanges();

    expect(text(harness, '.page-label')).toBe('page 3');
    backend.verify();
  });

  it('clamps a page the API would reject instead of sending it', async () => {
    const { tokens, backend } = setup();
    signIn(tokens);

    await RouterTestingHarness.create('/routing?page=0');
    const request = backend.expectOne((r) => r.url === '/api/quotes');

    expect(request.request.params.get('page')).toBe('1');
    request.flush(PAGE);
    backend.verify();
  });

  it('abandons the request for the page that was left', async () => {
    const { tokens, backend } = setup();
    signIn(tokens);

    const harness = await RouterTestingHarness.create('/routing?page=1');
    const first = backend.expectOne((r) => r.params.get('page') === '1');

    await harness.navigateByUrl('/routing?page=2');
    const second = backend.expectOne((r) => r.params.get('page') === '2');

    expect(first.cancelled).toBe(true);

    second.flush({ ...PAGE, page: 2 });
    backend.verify();
  });
});
