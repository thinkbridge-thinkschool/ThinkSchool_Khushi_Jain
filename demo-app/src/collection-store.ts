import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  CollectionsApiService,
  MAXIMUM_ITEMS,
  type CollectionDetails,
} from './collections-api';
import { toAppError } from './http';

export type CollectionStatus = 'idle' | 'loading' | 'ready' | 'error';

/**
 * Day 16 piece 2. All of one collection's state, in signals, in a service.
 *
 * Two rules hold the design together.
 *
 * First, every writable signal is private and every public member is a readonly
 * signal or a computed. A component can read the state and call a method; it
 * can never assign to it. That is what makes the store the single writer, and
 * it is most of why this does not need a state library.
 *
 * Second, the server is the only source of truth about membership. The three
 * mutating endpoints answer 204 with no body, so there is nothing to merge --
 * every mutation is followed by a re-read of GET /api/collections/{id}. No
 * local guessing about what the collection now contains, which means no way for
 * the screen to disagree with the API.
 */
@Injectable({ providedIn: 'root' })
export class CollectionStore {
  private readonly api = inject(CollectionsApiService);
  private readonly destroyRef = inject(DestroyRef);

  private readonly openId = signal<number | null>(null);
  private readonly loadStatus = signal<CollectionStatus>('idle');
  private readonly details = signal<CollectionDetails | null>(null);
  private readonly loadError = signal('');
  private readonly actionError = signal('');
  private readonly pendingQuoteIds = signal<ReadonlySet<number>>(new Set());
  private readonly creating = signal(false);
  private readonly staleView = signal(false);

  readonly id = this.openId.asReadonly();
  readonly status = this.loadStatus.asReadonly();
  readonly error = this.loadError.asReadonly();
  readonly lastActionError = this.actionError.asReadonly();
  readonly isCreating = this.creating.asReadonly();
  readonly pending = this.pendingQuoteIds.asReadonly();

  /**
   * True when a mutation succeeded but the re-read that follows it did not, so
   * what is on screen is the collection as it was *before* that mutation. The
   * data is still worth showing -- it is only out of date, not wrong -- so this
   * is a separate fact from `error` and from `lastActionError`, and the page
   * says so rather than throwing the list away.
   */
  readonly isStale = this.staleView.asReadonly();

  readonly name = computed(() => this.details()?.name ?? '');
  readonly items = computed(() => this.details()?.items ?? []);

  /**
   * The API's own count, straight from CollectionDetails.itemCount. It is the
   * aggregate's Items.Count.
   */
  readonly itemCount = computed(() => this.details()?.itemCount ?? 0);

  /** How many of those the API actually returned rows for. */
  readonly listedCount = computed(() => this.items().length);

  /**
   * The gap between the two, and the reason both are kept rather than deriving
   * one from the other.
   *
   * itemCount counts every membership the aggregate holds. `items` is built by
   * joining those memberships to Quotes with `where !quote.IsDeleted`. So a
   * membership whose quote was soft-deleted -- or never existed, since
   * AddQuoteToCollectionHandler checks only that the *collection* exists and
   * CollectionItem has no foreign key to Quotes -- is counted and not listed.
   *
   * Deriving the count from items.length would quietly contradict the API;
   * showing itemCount alone would claim rows the list cannot account for.
   * Keeping both, and naming the difference, is the only honest option.
   */
  readonly unlistedCount = computed(() => Math.max(0, this.itemCount() - this.listedCount()));

  readonly isOpen = computed(() => this.openId() !== null);
  readonly isEmpty = computed(() => this.loadStatus() === 'ready' && this.itemCount() === 0);
  readonly isFull = computed(() => this.itemCount() >= MAXIMUM_ITEMS);
  readonly isBusy = computed(() => this.pendingQuoteIds().size > 0);

  /** Quote ids the collection currently lists, for a picker to mark as added. */
  readonly memberQuoteIds = computed(
    () => new Set(this.items().map((item) => item.quoteId)),
  );

  /**
   * Bumped on every re-read. A response whose generation is stale is dropped,
   * which is what keeps concurrent mutations honest: three adds in flight mean
   * three re-reads, and only the newest one may be applied. Without this the
   * screen can settle on whichever response happened to arrive last.
   */
  private refreshGeneration = 0;

  create(name: string): void {
    if (this.creating()) {
      return;
    }

    this.creating.set(true);
    this.actionError.set('');

    this.api
      .create(name)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (created) => {
          this.creating.set(false);
          // Deliberately not stored as state. The 201 is the aggregate shape:
          // no itemCount, and items without author or text. Only its id is
          // used, and the read model is fetched for everything else.
          this.open(created.id);
        },
        error: (failure: unknown) => {
          this.creating.set(false);
          this.actionError.set(toAppError(failure).message);
        },
      });
  }

  open(id: number): void {
    this.openId.set(id);
    this.details.set(null);
    this.loadError.set('');
    this.actionError.set('');
    this.pendingQuoteIds.set(new Set());
    this.staleView.set(false);
    this.loadStatus.set('loading');
    this.reread('foreground');
  }

  close(): void {
    this.refreshGeneration += 1; // abandon anything still in flight
    this.openId.set(null);
    this.details.set(null);
    this.loadStatus.set('idle');
    this.loadError.set('');
    this.actionError.set('');
    this.pendingQuoteIds.set(new Set());
    this.staleView.set(false);
  }

  refresh(): void {
    if (this.openId() === null) {
      return;
    }

    this.loadStatus.set('loading');
    this.reread('foreground');
  }

  add(quoteId: number): void {
    const collectionId = this.openId();

    // Guarding on the pending set is what makes a double-click harmless: the
    // second add never leaves the browser, so the API never has to answer
    // "already in this collection" for a click the user did not mean twice.
    if (collectionId === null || this.pendingQuoteIds().has(quoteId)) {
      return;
    }

    this.markPending(quoteId);
    this.actionError.set('');

    this.api
      .addItem(collectionId, quoteId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => this.settle(quoteId),
        error: (failure: unknown) => this.settle(quoteId, failure),
      });
  }

  remove(quoteId: number): void {
    const collectionId = this.openId();

    if (collectionId === null || this.pendingQuoteIds().has(quoteId)) {
      return;
    }

    this.markPending(quoteId);
    this.actionError.set('');

    this.api
      .removeItem(collectionId, quoteId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => this.settle(quoteId),
        error: (failure: unknown) => this.settle(quoteId, failure),
      });
  }

  private markPending(quoteId: number): void {
    this.pendingQuoteIds.update((pending) => new Set(pending).add(quoteId));
  }

  /**
   * A mutation has finished, successfully or not. Either way the collection is
   * re-read: a failure is as much a reason to re-sync as a success, because a
   * 400 means what is on screen and what the server holds have already
   * disagreed once.
   */
  private settle(quoteId: number, failure?: unknown): void {
    this.pendingQuoteIds.update((pending) => {
      const next = new Set(pending);
      next.delete(quoteId);

      return next;
    });

    if (failure !== undefined) {
      this.actionError.set(toAppError(failure).message);
    }

    this.reread('background');
  }

  /**
   * `foreground` is an open or an explicit Refresh: the user asked for this
   * collection, so if it cannot be read there is nothing to show and the error
   * owns the screen.
   *
   * `background` is the re-sync that follows a mutation. Here a collection is
   * already on screen and the user asked for an add or a remove, not for a
   * read. Failing that read does not make the rows already displayed wrong --
   * only out of date -- so the list stays up and `isStale` says why. Blanking a
   * loaded collection because a follow-up GET hit a 500 loses data the user
   * still wants over a request they never made.
   *
   * The exception is a background re-read with nothing loaded behind it, which
   * happens if a mutation is fired while the collection is in its error state.
   * There is nothing to preserve, so it is treated as a foreground failure.
   */
  private reread(mode: 'foreground' | 'background'): void {
    const collectionId = this.openId();

    if (collectionId === null) {
      return;
    }

    const generation = ++this.refreshGeneration;

    this.api
      .getById(collectionId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (loaded) => {
          if (generation !== this.refreshGeneration) {
            return; // a newer re-read is already on its way
          }

          this.details.set(loaded);
          this.loadStatus.set('ready');
          this.staleView.set(false);
        },
        error: (failure: unknown) => {
          if (generation !== this.refreshGeneration) {
            return;
          }

          const error = toAppError(failure);

          if (mode === 'background' && this.details() !== null) {
            this.staleView.set(true);

            return;
          }

          this.loadError.set(
            error.kind === 'not-found'
              ? `Collection ${collectionId} was not found.`
              : error.message,
          );
          this.loadStatus.set('error');
        },
      });
  }
}
