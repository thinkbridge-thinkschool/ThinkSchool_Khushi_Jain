import { Component, DestroyRef, computed, effect, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import type { Subscription } from 'rxjs';
import { toAppError, type QuoteDetail } from './http';
import { QuotesApiService, parseQuoteId } from './quotes-api';

type State =
  | { kind: 'loading' }
  | { kind: 'ready'; quote: QuoteDetail }
  | { kind: 'error'; message: string; canRetry: boolean };

/**
 * Day 16. The detail half of the routed pair, at `/routing/:id`. This file is
 * reached only through the route's `loadComponent`, so nothing else in the app
 * imports it and the bundler can put it in its own chunk.
 */
@Component({
  selector: 'app-routed-detail-page',
  imports: [RouterLink],
  template: `
    <!--
      Same naming rule as the list card, so the browser has an old and a new
      box with the same name and animates between them.
    -->
    <section class="card" [style.view-transition-name]="transitionName()">
      <div class="row">
        <a class="nav-link" routerLink="/routing" queryParamsHandling="preserve">← All quotes</a>
      </div>

      @switch (state().kind) {
        @case ('loading') {
          <p class="state" role="status">Loading quote {{ id() }}…</p>
        }
        @case ('error') {
          <p class="banner bad" role="alert">{{ errorMessage() }}</p>
          @if (canRetry()) {
            <div class="row">
              <button class="quiet" (click)="retry()">Try again</button>
            </div>
          }
        }
        @case ('ready') {
          @if (quote(); as loaded) {
            <h2>{{ loaded.text }}</h2>
            <dl class="detail">
              <dt>Id</dt>
              <dd>{{ loaded.id }}</dd>
              <dt>Author</dt>
              <dd>{{ loaded.author }}</dd>
              <dt>Owner</dt>
              <dd>{{ loaded.ownerId ?? '(none)' }}</dd>
            </dl>
          }
        }
      }
    </section>
  `,
})
export class RoutedDetailPageComponent {
  private readonly quotesApi = inject(QuotesApiService);
  private readonly destroyRef = inject(DestroyRef);

  // Bound from the `:id` route parameter by withComponentInputBinding(). It is
  // the raw segment, so it is a string and may be anything.
  readonly id = input.required<string>();

  readonly state = signal<State>({ kind: 'loading' });

  readonly quote = computed(() => {
    const state = this.state();

    return state.kind === 'ready' ? state.quote : null;
  });

  readonly errorMessage = computed(() => {
    const state = this.state();

    return state.kind === 'error' ? state.message : '';
  });

  readonly canRetry = computed(() => {
    const state = this.state();

    return state.kind === 'error' && state.canRetry;
  });

  // Only a real id gets a name. `quote-` plus arbitrary URL text would not
  // always be a valid CSS custom-ident, and a name nothing can pair with is
  // worse than no name.
  readonly transitionName = computed(() => {
    const id = parseQuoteId(this.id());

    return id === null ? null : `quote-${id}`;
  });

  private readonly reloads = signal(0);
  private inFlight: Subscription | null = null;

  constructor() {
    effect(() => {
      this.reloads();
      this.load(this.id());
    });
  }

  retry(): void {
    this.reloads.update((count) => count + 1);
  }

  private load(rawId: string): void {
    this.inFlight?.unsubscribe();

    const id = parseQuoteId(rawId);

    // The route pattern `:id` matches any segment, so /routing/abc reaches this
    // component. The API's route is constrained to `{id:int}` and answers 404
    // for anything else, so there is nothing to ask it.
    if (id === null) {
      this.state.set({
        kind: 'error',
        message: `"${rawId}" is not a quote id, so there is nothing to look up.`,
        canRetry: false,
      });

      return;
    }

    this.state.set({ kind: 'loading' });

    this.inFlight = this.quotesApi
      .getById(id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (quote) => this.state.set({ kind: 'ready', quote }),
        error: (failure: unknown) => {
          const error = toAppError(failure);
          console.error(`Failed to load quote ${id}`, error);

          // 'That was not found.' is the interceptor's wording for any 404, so
          // only the id it applies to needs adding. A missing quote is not
          // worth retrying; a network or server failure is.
          this.state.set(
            error.kind === 'not-found'
              ? { kind: 'error', message: `Quote ${id} was not found.`, canRetry: false }
              : { kind: 'error', message: error.message, canRetry: true },
          );
        },
      });
  }
}
