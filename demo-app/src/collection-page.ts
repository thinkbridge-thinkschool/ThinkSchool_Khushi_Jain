import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CollectionStore } from './collection-store';
import { MAXIMUM_ITEMS, MINIMUM_NAME_LENGTH } from './collections-api';
import { toAppError, type QuoteSummary } from './http';
import { QuotesApiService } from './quotes-api';

const PICKER_SIZE = 8;

/**
 * Day 16 piece 2. The screen is a reader: it holds the two half-typed form
 * fields and the quote picker's own list, and nothing else. Every fact about
 * the collection comes from CollectionStore as a signal.
 */
@Component({
  selector: 'app-collection-page',
  template: `
    <section class="card">
      <h2>Collection</h2>
      <p class="hint">
        One collection's state in signals, in a service, over
        <code>/api/collections</code>.
      </p>

      @if (!store.isOpen()) {
        <p class="state">
          The API has no "list my collections" endpoint — only create, and read one by id. So
          either make one or open one you know the id of.
        </p>

        <div class="field">
          <label for="collection-name">New collection name</label>
          <input
            id="collection-name"
            [value]="draftName()"
            (input)="draftName.set($any($event.target).value)"
            [attr.aria-invalid]="nameTooShort() ? 'true' : null"
          />
          @if (nameTooShort()) {
            <p class="field-errors">At least {{ minimumNameLength }} characters.</p>
          }
        </div>

        <div class="row">
          <button [disabled]="nameTooShort() || store.isCreating()" (click)="create()">
            @if (store.isCreating()) {
              Creating…
            } @else {
              Create
            }
          </button>
        </div>

        <div class="field">
          <label for="collection-id">…or open an existing one by id</label>
          <input
            id="collection-id"
            inputmode="numeric"
            [value]="draftId()"
            (input)="draftId.set($any($event.target).value)"
          />
        </div>

        <div class="row">
          <button class="quiet" [disabled]="!parsedId()" (click)="open()">Open</button>
        </div>
      } @else {
        <div class="row">
          <strong>{{ store.name() || '…' }}</strong>
          <span class="page-label">#{{ store.id() }}</span>
          <button class="quiet" (click)="store.refresh()">Refresh</button>
          <button class="quiet" (click)="store.close()">Close</button>
        </div>

        @switch (store.status()) {
          @case ('loading') {
            <p class="state" role="status">Loading collection {{ store.id() }}…</p>
          }
          @case ('error') {
            <p class="banner bad" role="alert">{{ store.error() }}</p>
          }
          @case ('ready') {
            @if (store.isStale()) {
              <p class="banner warn" role="status">
                The change went through, but re-reading the collection failed, so this list is
                what the API held before it. Refresh to try again.
              </p>
            }

            <p class="count">
              {{ store.itemCount() }} counted by the API, {{ store.listedCount() }} listed
              @if (store.isFull()) {
                <span class="orphan">full — {{ maximumItems }} is the limit</span>
              }
            </p>

            <!--
              The one piece of UI that exists purely because of what the API
              really does. See CollectionStore.unlistedCount.
            -->
            @if (store.unlistedCount() > 0) {
              <p class="banner bad" role="status">
                {{ store.unlistedCount() }} membership(s) counted but not listed. The API's
                <code>itemCount</code> is the aggregate's, while <code>items</code> is joined to
                quotes that still exist — so a quote that was deleted, or never existed, is
                counted and not shown.
              </p>
            }

            @if (store.isEmpty()) {
              <p class="state">Nothing in this collection yet.</p>
            } @else {
              <ul class="quotes">
                @for (item of store.items(); track item.quoteId) {
                  <li>
                    <span class="text">{{ item.text }}</span>
                    <span class="by">
                      {{ item.author }} · #{{ item.quoteId }}
                      <button
                        class="inline-action"
                        [disabled]="store.pending().has(item.quoteId)"
                        (click)="store.remove(item.quoteId)"
                      >
                        @if (store.pending().has(item.quoteId)) {
                          removing…
                        } @else {
                          remove
                        }
                      </button>
                    </span>
                  </li>
                }
              </ul>
            }
          }
        }

        @if (store.lastActionError()) {
          <p class="banner bad" role="alert">{{ store.lastActionError() }}</p>
        }
      }
    </section>

    @if (store.isOpen()) {
      <section class="card">
        <h2>Add a quote</h2>

        @switch (pickerStatus()) {
          @case ('loading') {
            <p class="state" role="status">Loading quotes…</p>
          }
          @case ('error') {
            <p class="banner bad" role="alert">{{ pickerError() }}</p>
            <div class="row">
              <button class="quiet" (click)="loadPicker()">Try again</button>
            </div>
          }
          @default {
            @if (pickerQuotes().length === 0) {
              <p class="state">No quotes exist yet. Seed some first.</p>
            } @else {
              <ul class="quotes">
                @for (quote of pickerQuotes(); track quote.id) {
                  <li>
                    <span class="text">{{ quote.text }}</span>
                    <span class="by">
                      {{ quote.author }} · #{{ quote.id }}
                      @if (store.memberQuoteIds().has(quote.id)) {
                        <span class="orphan">already added</span>
                      } @else {
                        <button
                          class="inline-action"
                          [disabled]="store.pending().has(quote.id) || store.isFull()"
                          (click)="store.add(quote.id)"
                        >
                          @if (store.pending().has(quote.id)) {
                            adding…
                          } @else {
                            add
                          }
                        </button>
                      }
                    </span>
                  </li>
                }
              </ul>
            }
          }
        }
      </section>
    }
  `,
})
export class CollectionPageComponent {
  readonly store = inject(CollectionStore);

  private readonly quotesApi = inject(QuotesApiService);
  private readonly destroyRef = inject(DestroyRef);

  readonly minimumNameLength = MINIMUM_NAME_LENGTH;
  readonly maximumItems = MAXIMUM_ITEMS;

  readonly draftName = signal('');
  readonly draftId = signal('');

  readonly nameTooShort = computed(() => this.draftName().trim().length < MINIMUM_NAME_LENGTH);
  readonly parsedId = computed(() => {
    const raw = this.draftId().trim();

    return /^\d+$/.test(raw) ? Number(raw) : null;
  });

  readonly pickerStatus = signal<'loading' | 'ready' | 'error'>('loading');
  readonly pickerQuotes = signal<QuoteSummary[]>([]);
  readonly pickerError = signal('');

  constructor() {
    this.loadPicker();
  }

  create(): void {
    this.store.create(this.draftName().trim());
    this.draftName.set('');
  }

  open(): void {
    const id = this.parsedId();

    if (id !== null) {
      this.store.open(id);
      this.draftId.set('');
    }
  }

  loadPicker(): void {
    this.pickerStatus.set('loading');

    this.quotesApi
      .getPage(1, PICKER_SIZE)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (response) => {
          this.pickerQuotes.set(response.items);
          this.pickerStatus.set('ready');
        },
        error: (failure: unknown) => {
          this.pickerError.set(toAppError(failure).message);
          this.pickerStatus.set('error');
        },
      });
  }
}
