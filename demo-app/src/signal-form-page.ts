import { HttpClient } from '@angular/common/http';
import { Component, ElementRef, inject, signal, viewChild } from '@angular/core';
import {
  FormField,
  form,
  maxLength,
  required,
  requiredError,
  submit,
  validate,
  type FieldTree,
} from '@angular/forms/signals';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import {
  API_BASE_URL,
  isAppError,
  toAppError,
  type CreateQuoteFormValue,
  type CreatedQuote,
} from './http';
import { SessionStore } from './session';

/**
 * Day 14 piece 2. The same create-a-quote form as the Reactive Forms page,
 * rebuilt on the Signal Forms preview API: one signal holds the model, the
 * schema declares the rules, and submit() owns the pending state and folds
 * server-side errors back onto fields.
 */
@Component({
  selector: 'app-signal-form-page',
  imports: [FormField],
  template: `
    <section class="card">
      <h2>Create a quote — Signal Forms</h2>
      <p class="hint">
        One model signal, a schema of rules, and submit() for the pending state. Same endpoint and
        same limits as the Reactive Forms page, so the two are directly comparable.
      </p>

      @if (createdQuote(); as quote) {
        <p class="banner good" role="status">Quote #{{ quote.id }} created.</p>
      }

      <form (submit)="onSubmit($event)" novalidate>
        <div class="field">
          <label for="sf-author">Author</label>
          <input
            id="sf-author"
            #authorInput
            type="text"
            [formField]="quoteForm.author"
            [attr.aria-invalid]="
              quoteForm.author().touched() && quoteForm.author().invalid() ? true : null
            "
            [attr.aria-describedby]="
              quoteForm.author().touched() && quoteForm.author().invalid() ? 'sf-author-error' : null
            "
          />
          @if (quoteForm.author().touched() && quoteForm.author().invalid()) {
            <div id="sf-author-error" role="alert">
              <ul class="field-errors">
                @for (error of quoteForm.author().errors(); track error) {
                  <li>{{ error.message }}</li>
                }
              </ul>
            </div>
          }
        </div>

        <div class="field">
          <label for="sf-text">Text</label>
          <textarea
            id="sf-text"
            #textInput
            [formField]="quoteForm.text"
            [attr.aria-invalid]="
              quoteForm.text().touched() && quoteForm.text().invalid() ? true : null
            "
            [attr.aria-describedby]="
              quoteForm.text().touched() && quoteForm.text().invalid() ? 'sf-text-error' : null
            "
          ></textarea>
          @if (quoteForm.text().touched() && quoteForm.text().invalid()) {
            <div id="sf-text-error" role="alert">
              <ul class="field-errors">
                @for (error of quoteForm.text().errors(); track error) {
                  <li>{{ error.message }}</li>
                }
              </ul>
            </div>
          }
        </div>

        <div class="row">
          <button type="submit" [disabled]="quoteForm().submitting()">
            @if (quoteForm().submitting()) {
              Creating…
            } @else {
              Create quote
            }
          </button>
        </div>

        @if (quoteForm().errors().length > 0) {
          <p class="banner bad" role="alert">
            @for (error of quoteForm().errors(); track error) {
              {{ error.message }}
            }
          </p>
        }
      </form>
    </section>
  `,
})
export class SignalFormPageComponent {
  private readonly http = inject(HttpClient);
  private readonly session = inject(SessionStore);
  private readonly router = inject(Router);
  private readonly quotes = `${inject(API_BASE_URL)}/api/quotes`;

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
    // [formField] also reflects this as a native `maxlength="200"` attribute, so typing,
    // pasting, and Playwright's fill() all get silently capped at 200 chars before the
    // value can ever violate the rule -- this message is not reachable through normal
    // use; Reactive Forms has no such native reflection, so its equivalent error is
    // reachable by typing straight past the limit.
    maxLength(path.author, 200, { message: 'Author must be 200 characters or fewer.' });

    required(path.text, { message: 'Text is required.' });
    validate(path.text, (ctx) =>
      ctx.value().length > 0 && ctx.value().trim().length === 0
        ? requiredError({ message: 'Text is required.' })
        : undefined,
    );
    // Same native maxlength reflection as author's, above -- also not reachable in practice.
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
          this.http.post<CreatedQuote>(this.quotes, value),
        );
        this.createdQuote.set(created);
        this.model.set({ author: '', text: '' });

        return;
      } catch (failure) {
        return this.toSubmitErrors(failure, field);
      }
    });

    if (!success) {
      this.focusFirstInvalidField();
    }
  }

  /**
   * The error-mapping interceptor has already produced an AppError, so this
   * only has to decide which field each message belongs to. Field errors arrive
   * keyed by the C# property names the API used, Author and Text.
   */
  private toSubmitErrors(failure: unknown, field: FieldTree<CreateQuoteFormValue>) {
    const error = toAppError(failure);
    console.error('Create failed', error);

    if (isAppError(failure)) {
      const entries = Object.entries(error.fieldErrors);

      if (entries.length > 0) {
        return entries.flatMap(([key, messages]) => {
          const target = key === 'Author' ? field.author : key === 'Text' ? field.text : undefined;

          return messages.map((message) =>
            target ? { kind: 'server', message, fieldTree: target } : { kind: 'server', message },
          );
        });
      }

      // A 401 that survived the refresh interceptor means the session is gone,
      // not that this one request was unlucky.
      if (error.kind === 'unauthorized' && !this.session.isSignedIn()) {
        this.router.navigate(['/login']);
      }
    }

    return { kind: 'server', message: error.message };
  }

  private focusFirstInvalidField(): void {
    if (this.quoteForm.author().invalid()) {
      this.authorInput()?.nativeElement.focus();
    } else if (this.quoteForm.text().invalid()) {
      this.textInput()?.nativeElement.focus();
    }
  }
}
