import { HttpClient } from '@angular/common/http';
import { Component, computed, effect, inject, signal } from '@angular/core';
import { API_BASE_URL, toAppError, type QuoteSummary, type QuotesPage } from './http';

const PAGE_SIZE = 5;

/**
 * Day 13 piece 1. State is signal()/computed()/effect() only, the template is
 * the built-in control flow, and the service is reached with inject(). Nothing
 * here subscribes to a router param or a form -- the page exists to show that a
 * zoneless app re-renders from signal reads alone.
 */
@Component({
  selector: 'app-signals-page',
  template: `
    <section class="card">
      <h2>Signals, zoneless, standalone</h2>
      <p class="hint">
        The page filter is a computed() over the loaded page, so it costs no request. Paging runs
        through an effect() that reads page().
      </p>

      <div class="field">
        <label for="author-filter">Author contains</label>
        <input
          id="author-filter"
          [value]="author()"
          (input)="author.set($any($event.target).value)"
        />
      </div>

      <div class="row">
        <button class="quiet" (click)="page.set(page() - 1)" [disabled]="page() === 1">Previous</button>
        <span class="page-label">page {{ page() }}</span>
        <button class="quiet" (click)="page.set(page() + 1)" [disabled]="isLastPage()">Next</button>
      </div>

      @switch (status()) {
        @case ('loading') {
          <p class="state" role="status">Loading…</p>
        }
        @case ('error') {
          <p class="banner bad" role="alert">{{ error() }}</p>
        }
        @default {
          @if (visible().length === 0) {
            @if (total() === 0) {
              <p class="state">
                No quotes yet. Run <code>./seed-quotes.sh</code> to add some, then reload.
              </p>
            } @else {
              <p class="state">Nothing on this page matches that filter.</p>
            }
          } @else {
            <ul class="quotes">
              @for (quote of visible(); track quote.id) {
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
            <p class="count">{{ visible().length }} shown of {{ total() }} total</p>
          }
        }
      }
    </section>
  `,
})
export class SignalsPageComponent {
  private readonly http = inject(HttpClient);
  private readonly quotes = `${inject(API_BASE_URL)}/api/quotes`;

  readonly page = signal(1);
  readonly author = signal('');
  readonly total = signal(0);
  readonly status = signal<'loading' | 'ready' | 'error'>('loading');
  readonly error = signal('');

  private readonly loaded = signal<QuoteSummary[]>([]);

  readonly visible = computed(() => {
    const needle = this.author().trim().toLowerCase();

    return this.loaded().filter((quote) => quote.author.toLowerCase().includes(needle));
  });

  readonly isLastPage = computed(() => this.page() * PAGE_SIZE >= this.total());

  constructor() {
    effect(() => this.load(this.page()));
  }

  private load(page: number): void {
    this.status.set('loading');

    this.http.get<QuotesPage>(this.quotes, { params: { page, size: PAGE_SIZE } }).subscribe({
      next: (response) => {
        // A newer page was asked for while this one was in flight.
        if (this.page() !== page) {
          return;
        }

        this.loaded.set(response.items);
        this.total.set(response.total);
        this.status.set('ready');
      },
      // Every failure arrives here already mapped to an AppError by the
      // interceptor, so the message is safe to render as it stands.
      error: (failure: unknown) => {
        if (this.page() !== page) {
          return;
        }

        this.error.set(toAppError(failure).message);
        this.status.set('error');
      },
    });
  }
}
