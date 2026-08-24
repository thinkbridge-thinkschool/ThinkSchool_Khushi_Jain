import { HttpClient, HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { Component, Injectable, computed, effect, inject, signal } from '@angular/core';
import { bootstrapApplication } from '@angular/platform-browser';

const PAGE_SIZE = 5;

// One item of GET /api/quotes' `items` array.
interface QuoteSummary {
  id: number;
  author: string;
  text: string;
  ownerId: string | null;
  ownerActive: boolean;
}

// The envelope GET /api/quotes returns.
interface QuotesPage {
  page: number;
  size: number;
  total: number;
  items: QuoteSummary[];
}

// GET /api/quotes/{id} returns the Quote entity directly, not a QuoteSummary:
// no ownerActive, and it carries isDeleted instead (always false on a 200,
// since the repository filters deleted rows out, but it's part of the shape).
interface QuoteDetail {
  id: number;
  author: string;
  text: string;
  ownerId: string | null;
  isDeleted: boolean;
}

@Injectable({ providedIn: 'root' })
class QuotesService {
  private readonly http = inject(HttpClient);

  getPage(page: number, size: number) {
    return this.http.get<QuotesPage>(`/api/quotes?page=${page}&size=${size}`);
  }

  getById(id: number) {
    return this.http.get<QuoteDetail>(`/api/quotes/${id}`);
  }
}

type Status = 'loading' | 'ready' | 'error';

@Component({
  selector: 'app-quotes',
  template: `
    <h1>Quotes</h1>

    <section>
      <p>
        <button (click)="page.set(page() - 1)" [disabled]="page() === 1">Previous</button>
        page {{ page() }}
        <button (click)="page.set(page() + 1)" [disabled]="isLastPage()">Next</button>
      </p>

      @switch (listStatus()) {
        @case ('loading') {
          <p>Loading…</p>
        }
        @case ('error') {
          <p>Could not read /api/quotes.</p>
        }
        @default {
          @if (quotes().length === 0) {
            <p>No quotes on this page.</p>
          } @else {
            <ul>
              @for (quote of quotes(); track quote.id) {
                <li>
                  <button
                    (click)="selectedId.set(quote.id)"
                    [attr.aria-current]="selectedId() === quote.id"
                  >
                    {{ quote.text }} — {{ quote.author }}
                  </button>
                  @if (!quote.ownerActive) {
                    <em>(no active owner)</em>
                  }
                </li>
              }
            </ul>
            <p>{{ quotes().length }} shown of {{ total() }} total</p>
          }
        }
      }
    </section>

    <section>
      <h2>Detail</h2>
      @switch (detailStatus()) {
        @case ('idle') {
          <p>Select a quote to see its detail.</p>
        }
        @case ('loading') {
          <p>Loading quote {{ selectedId() }}…</p>
        }
        @case ('error') {
          <p>{{ detailError() }}</p>
        }
        @case ('ready') {
          @if (detail(); as quote) {
            <dl>
              <dt>Id</dt>
              <dd>{{ quote.id }}</dd>
              <dt>Author</dt>
              <dd>{{ quote.author }}</dd>
              <dt>Text</dt>
              <dd>{{ quote.text }}</dd>
              <dt>Owner</dt>
              <dd>{{ quote.ownerId ?? '(none)' }}</dd>
            </dl>
          }
        }
      }
    </section>
  `,
})
export class QuotesComponent {
  private readonly quotesService = inject(QuotesService);

  readonly page = signal(1);
  readonly total = signal(0);
  readonly listStatus = signal<Status>('loading');
  readonly quotes = signal<QuoteSummary[]>([]);

  readonly selectedId = signal<number | null>(null);
  readonly detailStatus = signal<'idle' | Status>('idle');
  readonly detail = signal<QuoteDetail | null>(null);
  readonly detailError = signal('');

  readonly isLastPage = computed(() => this.page() * PAGE_SIZE >= this.total());

  // Bumped on every list/detail fetch so a response from a superseded
  // request can be told apart from the one currently being waited on.
  private listRequestId = 0;
  private detailRequestId = 0;

  constructor() {
    effect(() => this.loadList(this.page()));
    effect(() => this.loadDetail(this.selectedId()));
  }

  private loadList(page: number): void {
    const requestId = ++this.listRequestId;
    this.listStatus.set('loading');

    this.quotesService.getPage(page, PAGE_SIZE).subscribe({
      next: (response) => {
        if (requestId !== this.listRequestId) return; // superseded by a newer page request
        this.quotes.set(response.items);
        this.total.set(response.total);
        this.listStatus.set('ready');
      },
      error: (err: HttpErrorResponse) => {
        if (requestId !== this.listRequestId) return;
        console.error('Failed to load quotes', err);
        this.listStatus.set('error');
      },
    });
  }

  private loadDetail(id: number | null): void {
    const requestId = ++this.detailRequestId;

    if (id === null) {
      this.detailStatus.set('idle');
      this.detail.set(null);
      return;
    }

    this.detailStatus.set('loading');

    this.quotesService.getById(id).subscribe({
      next: (quote) => {
        if (requestId !== this.detailRequestId) return; // superseded by a newer selection
        this.detail.set(quote);
        this.detailStatus.set('ready');
      },
      error: (err: HttpErrorResponse) => {
        if (requestId !== this.detailRequestId) return;
        this.detailError.set(err.status === 404 ? 'Quote not found.' : 'Could not read /api/quotes/{id}.');
        this.detailStatus.set('error');
      },
    });
  }
}

bootstrapApplication(QuotesComponent, {
  providers: [provideHttpClient()],
}).catch((error) => console.error(error));
