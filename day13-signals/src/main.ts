import { HttpClient, provideHttpClient } from '@angular/common/http';
import { Component, computed, effect, inject, signal } from '@angular/core';
import { bootstrapApplication } from '@angular/platform-browser';

const PAGE_SIZE = 5;

// The body of GET /api/quotes.
interface QuotesPage {
  page: number;
  size: number;
  total: number;
  items: {
    id: number;
    author: string;
    text: string;
    ownerId: string | null;
    ownerActive: boolean;
  }[];
}

@Component({
  selector: 'app-quotes',
  template: `
    <h1>Quotes</h1>

    <label>
      Author contains
      <input [value]="author()" (input)="author.set($any($event.target).value)" />
    </label>

    <p>
      <button (click)="page.set(page() - 1)" [disabled]="page() === 1">Previous</button>
      page {{ page() }}
      <button (click)="page.set(page() + 1)" [disabled]="isLastPage()">Next</button>
    </p>

    @switch (status()) {
      @case ('loading') {
        <p>Loading…</p>
      }
      @case ('error') {
        <p>Could not read /api/quotes.</p>
      }
      @default {
        <p>{{ visible().length }} shown of {{ total() }} total</p>

        @if (visible().length === 0) {
          <p>Nothing on this page matches.</p>
        } @else {
          <ul>
            @for (quote of visible(); track quote.id) {
              <li>
                {{ quote.text }} — {{ quote.author }}
                @if (!quote.ownerActive) {
                  <em>(no active owner)</em>
                }
              </li>
            }
          </ul>
        }
      }
    }
  `,
})
export class QuotesComponent {
  private readonly http = inject(HttpClient);

  readonly page = signal(1);
  readonly author = signal('');
  readonly total = signal(0);
  readonly status = signal<'loading' | 'ready' | 'error'>('loading');

  private readonly quotes = signal<QuotesPage['items']>([]);

  readonly visible = computed(() => {
    const needle = this.author().trim().toLowerCase();
    return this.quotes().filter((quote) => quote.author.toLowerCase().includes(needle));
  });

  readonly isLastPage = computed(() => this.page() * PAGE_SIZE >= this.total());

  constructor() {
    effect(() => this.load(this.page()));
  }

  private load(page: number): void {
    this.status.set('loading');

    this.http
      .get<QuotesPage>(`/api/quotes?page=${page}&size=${PAGE_SIZE}`)
      .subscribe({
        next: (response) => {
          // A newer page was asked for while this one was in flight.
          if (this.page() !== page) {
            return;
          }

          this.quotes.set(response.items);
          this.total.set(response.total);
          this.status.set('ready');
        },
        error: () => this.status.set('error'),
      });
  }
}

bootstrapApplication(QuotesComponent, {
  providers: [provideHttpClient()],
}).catch((error) => console.error(error));
