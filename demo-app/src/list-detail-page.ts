import { HttpClient } from '@angular/common/http';
import { Component, Injectable, computed, effect, inject, signal } from '@angular/core';
import {
  API_BASE_URL,
  toAppError,
  type QuoteDetail,
  type QuoteSummary,
  type QuotesPage,
} from './http';

const PAGE_SIZE = 5;

@Injectable({ providedIn: 'root' })
export class ListDetailQuotesService {
  private readonly http = inject(HttpClient);
  private readonly quotes = `${inject(API_BASE_URL)}/api/quotes`;

  getPage(page: number, size: number) {
    return this.http.get<QuotesPage>(this.quotes, { params: { page, size } });
  }

  getById(id: number) {
    return this.http.get<QuoteDetail>(`${this.quotes}/${id}`);
  }
}

type Status = 'loading' | 'ready' | 'error';

/**
 * Day 13 piece 2. List and detail load independently, so their responses can
 * interleave: each fetch takes a request id and a response whose id is no
 * longer current is dropped rather than rendered.
 */
@Component({
  selector: 'app-list-detail-page',
  template: `
    <section class="card">
      <h2>List</h2>

      <div class="row">
        <button class="quiet" (click)="page.set(page() - 1)" [disabled]="page() === 1">Previous</button>
        <span class="page-label">page {{ page() }}</span>
        <button class="quiet" (click)="page.set(page() + 1)" [disabled]="isLastPage()">Next</button>
      </div>

      @switch (listStatus()) {
        @case ('loading') {
          <p class="state" role="status">Loading…</p>
        }
        @case ('error') {
          <p class="banner bad" role="alert">{{ listError() }}</p>
        }
        @default {
          @if (quotes().length === 0) {
            <p class="state">No quotes on this page.</p>
          } @else {
            <ul class="quotes">
              @for (quote of quotes(); track quote.id) {
                <li>
                  <button
                    class="link"
                    (click)="selectedId.set(quote.id)"
                    [attr.aria-current]="selectedId() === quote.id"
                  >
                    {{ quote.text }}
                  </button>
                  <span class="by">
                    {{ quote.author }}
                    @if (!quote.ownerActive) {
                      <span class="orphan">no active owner</span>
                    }
                  </span>
                </li>
              }
            </ul>
            <p class="count">{{ quotes().length }} shown of {{ total() }} total</p>
          }
        }
      }
    </section>

    <section class="card">
      <h2>Detail</h2>

      @switch (detailStatus()) {
        @case ('idle') {
          <p class="state">Select a quote to see its detail.</p>
        }
        @case ('loading') {
          <p class="state" role="status">Loading quote {{ selectedId() }}…</p>
        }
        @case ('error') {
          <p class="banner bad" role="alert">{{ detailError() }}</p>
        }
        @case ('ready') {
          @if (detail(); as quote) {
            <dl class="detail">
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
export class ListDetailPageComponent {
  private readonly quotesService = inject(ListDetailQuotesService);

  readonly page = signal(1);
  readonly total = signal(0);
  readonly listStatus = signal<Status>('loading');
  readonly listError = signal('');
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
      error: (failure: unknown) => {
        if (requestId !== this.listRequestId) return;
        const error = toAppError(failure);
        console.error('Failed to load quotes', error);
        this.listError.set(error.message);
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
      error: (failure: unknown) => {
        if (requestId !== this.detailRequestId) return;
        const error = toAppError(failure);
        // 'That was not found.' is already the interceptor's wording for a 404,
        // so only the id it applies to needs adding.
        this.detailError.set(
          error.kind === 'not-found' ? `Quote ${id} was not found.` : error.message,
        );
        this.detailStatus.set('error');
      },
    });
  }
}
