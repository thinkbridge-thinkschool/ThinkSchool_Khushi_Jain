import { HttpClient, HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { Component, ElementRef, inject, signal, viewChild } from '@angular/core';
import { bootstrapApplication } from '@angular/platform-browser';
import { FormField, form, maxLength, required, requiredError, submit, validate, type FieldTree } from '@angular/forms/signals';
import { firstValueFrom } from 'rxjs';

// Matches CreateQuoteRequest in QuoteController.cs / Contracts/QuoteRequests.cs.
interface CreateQuoteFormValue {
  author: string;
  text: string;
}

// The 201 body of POST /api/quotes: the Quote entity, same shape GET /api/quotes/{id} returns.
interface CreatedQuote {
  id: number;
  author: string;
  text: string;
  ownerId: string | null;
  isDeleted: boolean;
}

// The automatic minimal-API validation filter's 400 body (RFC 9110 ValidationProblemDetails).
// Its `errors` keys are the C# record's property names as-is: PascalCase, not camelCase.
interface ValidationProblemDetails {
  title: string;
  status: number;
  errors?: Record<string, string[]>;
}

@Component({
  selector: 'app-create-quote',
  imports: [FormField],
  template: `
    <main>
      <h1>Create a quote</h1>

      @if (createdQuote(); as quote) {
        <p class="banner banner-success" role="status">Quote #{{ quote.id }} created.</p>
      }

      <form (submit)="onSubmit($event)" novalidate>
        <div class="field field-token">
          <label for="bearer-token">Bearer token (dev testing only)</label>
          <input
            id="bearer-token"
            type="text"
            [value]="bearerToken()"
            (input)="bearerToken.set($any($event.target).value)"
          />
        </div>

        <div class="field">
          <label for="author">Author</label>
          <input
            id="author"
            #authorInput
            type="text"
            [formField]="quoteForm.author"
            [attr.aria-invalid]="quoteForm.author().touched() && quoteForm.author().invalid() ? true : null"
            [attr.aria-describedby]="quoteForm.author().touched() && quoteForm.author().invalid() ? 'author-error' : null"
          />
          @if (quoteForm.author().touched() && quoteForm.author().invalid()) {
            <div id="author-error" role="alert">
              <ul class="field-errors">
                @for (error of quoteForm.author().errors(); track error) {
                  <li>{{ error.message }}</li>
                }
              </ul>
            </div>
          }
        </div>

        <div class="field">
          <label for="text">Text</label>
          <textarea
            id="text"
            #textInput
            [formField]="quoteForm.text"
            [attr.aria-invalid]="quoteForm.text().touched() && quoteForm.text().invalid() ? true : null"
            [attr.aria-describedby]="quoteForm.text().touched() && quoteForm.text().invalid() ? 'text-error' : null"
          ></textarea>
          @if (quoteForm.text().touched() && quoteForm.text().invalid()) {
            <div id="text-error" role="alert">
              <ul class="field-errors">
                @for (error of quoteForm.text().errors(); track error) {
                  <li>{{ error.message }}</li>
                }
              </ul>
            </div>
          }
        </div>

        @if (quoteForm().errors().length > 0) {
          <p class="banner banner-error" role="alert">
            @for (error of quoteForm().errors(); track error) {
              {{ error.message }}
            }
          </p>
        }

        <button type="submit" [disabled]="quoteForm().submitting()">
          @if (quoteForm().submitting()) {
            Creating…
          } @else {
            Create quote
          }
        </button>
      </form>
    </main>
  `,
  styles: `
    :host {
      display: block;
      max-width: 32rem;
      margin: 2.5rem auto;
      padding: 0 1rem;
      font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
      color: #1c1c1e;
    }

    h1 {
      font-size: 1.5rem;
      margin: 0 0 1.5rem;
    }

    form {
      display: flex;
      flex-direction: column;
      gap: 1.25rem;
    }

    .field {
      display: flex;
      flex-direction: column;
      gap: 0.375rem;
    }

    label {
      font-weight: 600;
      font-size: 0.9rem;
    }

    input[type='text'],
    textarea {
      box-sizing: border-box;
      width: 100%;
      padding: 0.5rem 0.65rem;
      border: 1px solid #c7c7cc;
      border-radius: 6px;
      font: inherit;
      font-size: 0.95rem;
    }

    textarea {
      min-height: 6rem;
      resize: vertical;
    }

    input:focus-visible,
    textarea:focus-visible {
      outline: 2px solid #2563eb;
      outline-offset: 1px;
    }

    input[aria-invalid='true'],
    textarea[aria-invalid='true'] {
      border-color: #d32f2f;
    }

    .field-token input {
      font-family: ui-monospace, Consolas, monospace;
      font-size: 0.8rem;
      color: #555;
    }

    .field-errors {
      list-style: none;
      margin: 0;
      padding: 0;
      color: #d32f2f;
      font-size: 0.85rem;
    }

    .banner {
      margin: 0;
      padding: 0.75rem 1rem;
      border-radius: 6px;
      font-size: 0.9rem;
    }

    .banner-success {
      background: #edf7ed;
      border: 1px solid #b6dfb8;
      color: #1e4620;
    }

    .banner-error {
      background: #fdeded;
      border: 1px solid #f3c6c6;
      color: #7d1f1f;
    }

    button[type='submit'] {
      align-self: flex-start;
      padding: 0.55rem 1.4rem;
      background: #2563eb;
      color: #fff;
      border: none;
      border-radius: 6px;
      font-size: 0.95rem;
      font-weight: 600;
      cursor: pointer;
    }

    button[type='submit']:disabled {
      background: #93c5fd;
      cursor: not-allowed;
    }
  `,
})
export class CreateQuoteComponent {
  private readonly http = inject(HttpClient);

  readonly bearerToken = signal('');
  readonly createdQuote = signal<CreatedQuote | null>(null);

  private readonly model = signal<CreateQuoteFormValue>({ author: '', text: '' });

  // required() only rejects a literal empty string; the API rejects whitespace-only too
  // (Quote.Create does IsNullOrWhiteSpace), so a matching check is added alongside it.
  readonly quoteForm = form(this.model, (path) => {
    required(path.author, { message: 'Author is required.' });
    validate(path.author, (ctx) =>
      ctx.value().length > 0 && ctx.value().trim().length === 0
        ? requiredError({ message: 'Author is required.' })
        : undefined,
    );
    maxLength(path.author, 200, { message: 'Author must be 200 characters or fewer.' });

    required(path.text, { message: 'Text is required.' });
    validate(path.text, (ctx) =>
      ctx.value().length > 0 && ctx.value().trim().length === 0
        ? requiredError({ message: 'Text is required.' })
        : undefined,
    );
    maxLength(path.text, 1000, { message: 'Text must be 1000 characters or fewer.' });
  });

  private readonly authorInput = viewChild<ElementRef<HTMLInputElement>>('authorInput');
  private readonly textInput = viewChild<ElementRef<HTMLTextAreaElement>>('textInput');

  async onSubmit(event: Event): Promise<void> {
    event.preventDefault();
    this.createdQuote.set(null);

    const success = await submit(this.quoteForm, async (field) => {
      const value = field().value();

      try {
        const created = await firstValueFrom(
          this.http.post<CreatedQuote>('/api/quotes', value, {
            headers: this.bearerToken() ? { Authorization: `Bearer ${this.bearerToken()}` } : {},
          }),
        );
        this.createdQuote.set(created);
        return;
      } catch (err) {
        return this.toSubmitErrors(err, field);
      }
    });

    if (!success) {
      this.focusFirstInvalidField();
    }
  }

  private toSubmitErrors(err: unknown, field: FieldTree<CreateQuoteFormValue>) {
    if (!(err instanceof HttpErrorResponse)) {
      return { kind: 'server', message: 'Could not create the quote.' };
    }

    if (err.status === 400) {
      const problem = err.error as ValidationProblemDetails | null;

      if (problem?.errors) {
        return Object.entries(problem.errors).flatMap(([key, messages]) => {
          const target = key === 'Author' ? field.author : key === 'Text' ? field.text : undefined;
          return messages.map((message) =>
            target ? { kind: 'server', message, fieldTree: target } : { kind: 'server', message },
          );
        });
      }
    }

    if (err.status === 401) {
      return { kind: 'server', message: 'Not authorized to create quotes. Provide a valid bearer token above.' };
    }

    return { kind: 'server', message: 'Could not create the quote.' };
  }

  private focusFirstInvalidField(): void {
    if (this.quoteForm.author().invalid()) {
      this.authorInput()?.nativeElement.focus();
    } else if (this.quoteForm.text().invalid()) {
      this.textInput()?.nativeElement.focus();
    }
  }
}

bootstrapApplication(CreateQuoteComponent, {
  providers: [provideHttpClient()],
}).catch((error) => console.error(error));
