import { Component, DestroyRef, computed, effect, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import type { Subscription } from 'rxjs';
import { toAppError, type QuoteSummary } from './http';
import { QuotesApiService } from './quotes-api';

const PAGE_SIZE = 5;

/**
 * The `page` query parameter arrives as whatever is in the URL bar. The API
 * answers 400 for a page below 1, so a junk value becomes page 1 here rather
 * than a failed request.
 */
function toPage(value: unknown): number {
  const page = Number(value);

  return Number.isSafeInteger(page) && page >= 1 ? page : 1;
}

type Status = 'loading' | 'ready' | 'error';

/**
 * Day 16. The list half of the routed pair, at `/routing`. Eagerly loaded, so
 * its code is in the initial bundle and the detail chunk's absence from the
 * network tab is visible against it.
 *
 * Unlike the List & detail tab, which keeps the selection in a signal, the
 * selection here is the URL: the detail is its own route, its own page and its
 * own bundle.
 */
@Component({
  selector: 'app-routed-list-page',
  imports: [RouterLink],
  template: `
    <section class="card">
      <h2>Routed list</h2>
      <p class="hint">Select a quote to open <code>/routing/:id</code>, a lazily loaded route.</p>

      @switch (status()) {
        @case ('loading') {
          <p class="state" role="status">Loading page {{ page() }}…</p>
        }
        @case ('error') {
          <p class="banner bad" role="alert">{{ error() }}</p>
          <div class="row">
            <button class="quiet" (click)="retry()">Try again</button>
          </div>
        }
        @default {
          @if (quotes().length === 0) {
            <p class="state">No quotes on this page.</p>
          } @else {
            <ul class="quotes">
              @for (quote of quotes(); track quote.id) {
                <!--
                  The name is per quote, so the browser pairs this card with the
                  detail card for the same id and morphs between them. Nothing
                  is coordinated between the two pages: they agree because both
                  derive the name from the API's id.
                -->
                <li [style.view-transition-name]="'quote-' + quote.id">
                  <a
                    class="text"
                    [routerLink]="['/routing', quote.id]"
                    queryParamsHandling="preserve"
                  >
                    {{ quote.text }}
                  </a>
                  <span class="by">
                    {{ quote.author }} · #{{ quote.id }}
                    @if (!quote.ownerActive) {
                      <span class="orphan">no active owner</span>
                    }
                  </span>
                </li>
              }
            </ul>
          }

          <div class="row">
            <button class="quiet" [disabled]="page() === 1" (click)="goToPage(page() - 1)">
              Previous
            </button>
            <span class="page-label">page {{ page() }}</span>
            <button class="quiet" [disabled]="isLastPage()" (click)="goToPage(page() + 1)">
              Next
            </button>
            <span class="count">{{ quotes().length }} shown of {{ total() }} total</span>
          </div>
        }
      }
    </section>
  `,
})
export class RoutedListPageComponent {
  private readonly quotesApi = inject(QuotesApiService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  // Bound from the `page` query parameter by withComponentInputBinding().
  readonly page = input(1, { transform: toPage });

  readonly status = signal<Status>('loading');
  readonly quotes = signal<QuoteSummary[]>([]);
  readonly total = signal(0);
  readonly error = signal('');

  readonly isLastPage = computed(() => this.page() * PAGE_SIZE >= this.total());

  private readonly reloads = signal(0);
  private inFlight: Subscription | null = null;

  constructor() {
    effect(() => {
      this.reloads();
      this.load(this.page());
    });
  }

  // Paging is a navigation, so the page number lives in the URL and survives
  // a trip to a detail page and back.
  goToPage(page: number): void {
    this.router.navigate([], { relativeTo: this.route, queryParams: { page } });
  }

  retry(): void {
    this.reloads.update((count) => count + 1);
  }

  private load(page: number): void {
    // Aborts the request for the page that was just left. Without this a slow
    // response for an earlier page could arrive last and be rendered.
    this.inFlight?.unsubscribe();
    this.status.set('loading');

    this.inFlight = this.quotesApi
      .getPage(page, PAGE_SIZE)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (response) => {
          this.quotes.set(response.items);
          this.total.set(response.total);
          this.status.set('ready');
        },
        error: (failure: unknown) => {
          const error = toAppError(failure);
          console.error('Failed to load quotes', error);
          this.error.set(error.message);
          this.status.set('error');
        },
      });
  }
}
