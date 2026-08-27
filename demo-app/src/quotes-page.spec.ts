import { HttpRequest, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { authInterceptor, errorMappingInterceptor, retryInterceptor } from './http';
import { QuotesPageComponent } from './quotes-page';
import { refreshInterceptor } from './session';

const PAGE = (page: number) => ({
  page,
  size: 5,
  total: 17,
  items: [{ id: page, author: 'Author', text: `Quote on page ${page}`, ownerId: null, ownerActive: false }],
});

const SERVER_500 = {
  title: 'An unexpected error occurred.',
  status: 500,
  traceId: '00-ee62e08973763fc09220479ab4be3794-42cd21ca1fa9860b-01',
};

const asked = (page: string) => (request: HttpRequest<unknown>) => request.params.get('page') === page;

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

  const backend = TestBed.inject(HttpTestingController);
  const component = TestBed.createComponent(QuotesPageComponent).componentInstance;

  backend.expectOne(asked('1')).flush(PAGE(1));

  return { component, backend };
}

describe('QuotesPageComponent', () => {
  afterEach(() => vi.useRealTimers());

  it('shows the first page once it loads', () => {
    const { component, backend } = setup();

    expect(component.status()).toBe('ready');
    expect(component.items()).toHaveLength(1);
    expect(component.total()).toBe(17);
    backend.verify();
  });

  it('shows the empty state for a page past the end', () => {
    const { component, backend } = setup();

    component.goToPage(9999);
    backend.expectOne(asked('9999')).flush({ page: 9999, size: 5, total: 17, items: [] });

    expect(component.status()).toBe('ready');
    expect(component.items()).toEqual([]);
    backend.verify();
  });

  it('shows the mapped message, not the API title, for a 400', () => {
    const { component, backend } = setup();

    component.loadInvalidSize();
    backend.expectOne((request) => request.params.get('size') === '0').flush(
      {
        title: 'One or more validation errors occurred.',
        status: 400,
        errors: { 'page/size': ['Page must be >= 1 and size must be between 1 and 100.'] },
      },
      { status: 400, statusText: 'Bad Request' },
    );

    expect(component.status()).toBe('error');
    expect(component.error()?.kind).toBe('validation');
    expect(component.error()?.message).toBe('Page must be >= 1 and size must be between 1 and 100.');
    backend.verify();
  });

  it('drops a superseded request instead of letting its retries overwrite a newer page', async () => {
    vi.useFakeTimers();
    const { component, backend } = setup();

    component.loadUnbindablePage();
    backend.expectOne(asked('abc')).flush(SERVER_500, { status: 500, statusText: 'Internal Server Error' });

    component.goToPage(2);
    backend.expectOne(asked('2')).flush(PAGE(2));

    expect(component.status()).toBe('ready');

    // The superseded request still had two retries and ~900ms of backoff left.
    await vi.advanceTimersByTimeAsync(5000);

    backend.expectNone(asked('abc'));
    expect(component.status()).toBe('ready');
    expect(component.items()).toEqual(PAGE(2).items);
    backend.verify();
  });
});
