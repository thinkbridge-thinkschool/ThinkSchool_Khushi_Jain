import { HttpClient } from '@angular/common/http';
import { Component, Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { type Subscription } from 'rxjs';
import {
  API_BASE_URL,
  RequestLog,
  toAppError,
  type AppError,
  type CreatedQuote,
  type QuoteSummary,
  type QuotesPage,
} from './http';
import { SessionStore } from './session';

const PAGE_SIZE = 5;

@Injectable({ providedIn: 'root' })
export class QuotesService {
  private readonly http = inject(HttpClient);
  private readonly quotes = `${inject(API_BASE_URL)}/api/quotes`;

  // page is loosely typed so the page can send a value the API cannot bind,
  // which is what the "force a server error" button needs.
  getPage(page: number | string, size: number) {
    return this.http.get<QuotesPage>(this.quotes, { params: { page, size } });
  }

  create(author: string, text: string) {
    return this.http.post<CreatedQuote>(this.quotes, { author, text });
  }

  createWithMalformedBody() {
    return this.http.post<CreatedQuote>(this.quotes, '{not-json', {
      headers: { 'Content-Type': 'application/json' },
    });
  }
}

type Status = 'loading' | 'ready' | 'error';

@Component({
  selector: 'app-quotes-page',
  template: `
    <section class="card">
      <div class="row">
        <button class="quiet" (click)="goToPage(page() - 1)" [disabled]="page() === 1">Previous</button>
        <span class="page-label">page {{ page() }} of {{ lastPage() }}</span>
        <button class="quiet" (click)="goToPage(page() + 1)" [disabled]="page() >= lastPage()">Next</button>
        <span class="spacer"></span>
        <button class="quiet" (click)="goToPage(9999)">empty page</button>
        <button class="quiet" (click)="loadInvalidSize()">size 0 → 400</button>
        <button class="quiet" (click)="loadUnbindablePage()">page=abc → 500, retried</button>
      </div>

      @switch (status()) {
        @case ('loading') {
          <p class="state" role="status">Loading…</p>
        }
        @case ('error') {
          <p class="banner bad" role="alert">{{ error()?.message }}</p>
        }
        @default {
          @if (items().length === 0) {
            <p class="state">No quotes on this page.</p>
          } @else {
            <ul class="quotes">
              @for (quote of items(); track quote.id) {
                <li>
                  <span class="text">{{ quote.text }}</span>
                  <span class="by">
                    {{ quote.author }}
                    @if (!quote.ownerActive) {
                      <span class="orphan">no active owner</span>
                    }
                  </span>
                </li>
              }
            </ul>
            <p class="count">{{ items().length }} shown of {{ total() }} total</p>
          }
        }
      }
    </section>

    <section class="card">
      <h2>Create a quote</h2>
      <p class="hint">
        POST /api/quotes needs the quotes.write scope. A 401 here is refreshed once and replayed
        before you ever see it.
      </p>

      <div class="row">
        <button (click)="createQuote()" [disabled]="posting()">Create a quote</button>
        <button class="quiet" (click)="createMalformed()" [disabled]="posting()">
          Malformed body → 500, not retried
        </button>
      </div>

      @if (created(); as quote) {
        <p class="banner good" role="status">Quote #{{ quote.id }} created.</p>
      }
      @if (createError(); as failure) {
        <p class="banner bad" role="alert">{{ failure.message }}</p>
      }
    </section>

    <section class="card">
      <h2>Retry log</h2>
      @if (log.lines().length === 0) {
        <p class="state">No retries yet.</p>
      } @else {
        <ul class="log">
          @for (line of log.lines(); track $index) {
            <li>{{ line }}</li>
          }
        </ul>
      }
    </section>
  `,
})
export class QuotesPageComponent {
  private readonly quotes = inject(QuotesService);
  private readonly session = inject(SessionStore);
  private readonly router = inject(Router);

  readonly log = inject(RequestLog);

  readonly page = signal(1);
  readonly status = signal<Status>('loading');
  readonly items = signal<QuoteSummary[]>([]);
  readonly total = signal(0);
  readonly error = signal<AppError | null>(null);

  readonly posting = signal(false);
  readonly created = signal<CreatedQuote | null>(null);
  readonly createError = signal<AppError | null>(null);

  readonly lastPage = computed(() => Math.max(1, Math.ceil(this.total() / PAGE_SIZE)));

  constructor() {
    this.load(this.page(), PAGE_SIZE);
  }

  goToPage(page: number): void {
    this.page.set(page);
    this.load(page, PAGE_SIZE);
  }

  loadInvalidSize(): void {
    this.load(this.page(), 0);
  }

  loadUnbindablePage(): void {
    this.load('abc', PAGE_SIZE);
  }

  createQuote(): void {
    this.posting.set(true);
    this.created.set(null);
    this.createError.set(null);

    this.quotes.create('Day 15', 'Sent from the Angular client.').subscribe({
      next: (quote) => {
        this.created.set(quote);
        this.posting.set(false);
      },
      error: (failure: unknown) => this.failCreate(failure),
    });
  }

  createMalformed(): void {
    this.posting.set(true);
    this.created.set(null);
    this.createError.set(null);

    this.quotes.createWithMalformedBody().subscribe({
      next: () => this.posting.set(false),
      error: (failure: unknown) => this.failCreate(failure),
    });
  }

  private inFlight?: Subscription;

  // A superseded page request has to be cancelled, not merely ignored: the
  // retry interceptor can hold a failing GET for another ~900ms of backoff,
  // which is long enough for it to land after a newer page has rendered and
  // replace it with an error.
  private load(page: number | string, size: number): void {
    this.inFlight?.unsubscribe();
    this.status.set('loading');
    this.error.set(null);

    this.inFlight = this.quotes.getPage(page, size).subscribe({
      next: (response) => {
        this.items.set(response.items);
        this.total.set(response.total);
        this.status.set('ready');
      },
      error: (failure: unknown) => {
        this.error.set(this.describe(failure));
        this.status.set('error');
      },
    });
  }

  private failCreate(failure: unknown): void {
    const error = this.describe(failure);
    this.createError.set(error);
    this.posting.set(false);

    // A 401 that survived the refresh interceptor means the session is gone,
    // not that this one request was unlucky.
    if (error.kind === 'unauthorized' && !this.session.isSignedIn()) {
      this.router.navigate(['/login']);
    }
  }

  // Only `message` reaches the page; status, traceId and field errors go to the
  // console so a failure stays diagnosable without being shown to the user.
  private describe(failure: unknown): AppError {
    const error = toAppError(failure);
    console.error('Request failed', error);

    return error;
  }
}
