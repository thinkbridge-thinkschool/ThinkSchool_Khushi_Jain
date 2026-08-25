import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Component, ElementRef, inject, signal, viewChild } from '@angular/core';
import { AbstractControl, FormControl, FormGroup, ReactiveFormsModule, ValidationErrors, Validators } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import type { CreatedQuote, ValidationProblemDetails } from './models';

// Not part of the shipped app (main.ts bootstraps SignalQuoteComponent only).
// Kept here, unwired, as the real Reactive Forms implementation the comparison
// in the README was actually tested against.
function requiredTrimmed(control: AbstractControl): ValidationErrors | null {
  const value = control.value;
  return typeof value === 'string' && value.trim().length > 0 ? null : { required: true };
}

@Component({
  selector: 'app-reactive-quote-for-comparison',
  imports: [ReactiveFormsModule],
  template: `
    <main>
      <h1>Create a quote (scratch: Reactive Forms, for verification only)</h1>

      @if (createdQuote(); as quote) {
        <p class="banner banner-success" role="status">Quote #{{ quote.id }} created.</p>
      }

      <form [formGroup]="quoteForm" (ngSubmit)="onSubmit()" novalidate>
        <div class="field field-token">
          <label for="rf-bearer-token">Bearer token (dev testing only)</label>
          <input
            id="rf-bearer-token"
            type="text"
            [value]="bearerToken()"
            (input)="bearerToken.set($any($event.target).value)"
          />
        </div>

        <div class="field">
          <label for="rf-author">Author</label>
          <input
            id="rf-author"
            #authorInput
            type="text"
            formControlName="author"
            [attr.aria-invalid]="authorControl.touched && authorControl.invalid ? true : null"
            [attr.aria-describedby]="authorControl.touched && authorControl.invalid ? 'rf-author-error' : null"
          />
          @if (authorControl.touched && authorControl.invalid) {
            <div id="rf-author-error" role="alert">
              <ul class="field-errors">
                @for (message of errorMessages(authorControl, 'Author'); track message) {
                  <li>{{ message }}</li>
                }
              </ul>
            </div>
          }
        </div>

        <div class="field">
          <label for="rf-text">Text</label>
          <textarea
            id="rf-text"
            #textInput
            formControlName="text"
            [attr.aria-invalid]="textControl.touched && textControl.invalid ? true : null"
            [attr.aria-describedby]="textControl.touched && textControl.invalid ? 'rf-text-error' : null"
          ></textarea>
          @if (textControl.touched && textControl.invalid) {
            <div id="rf-text-error" role="alert">
              <ul class="field-errors">
                @for (message of errorMessages(textControl, 'Text'); track message) {
                  <li>{{ message }}</li>
                }
              </ul>
            </div>
          }
        </div>

        @if (formError()) {
          <p class="banner banner-error" role="alert">{{ formError() }}</p>
        }

        <button type="submit" [disabled]="submitting()">
          @if (submitting()) {
            Creating…
          } @else {
            Create quote
          }
        </button>
      </form>
    </main>
  `,
  styles: `
    :host { display: block; font-family: sans-serif; }
    main { max-width: 32rem; margin: 0 auto; padding: 2.5rem 1.5rem; }
    form { display: flex; flex-direction: column; gap: 1.25rem; }
    .field { display: flex; flex-direction: column; gap: 0.375rem; }
    label { font-weight: 600; }
    input[type='text'], textarea { box-sizing: border-box; width: 100%; padding: 0.5rem; border: 1px solid #ccc; }
    input[aria-invalid='true'], textarea[aria-invalid='true'] { border-color: #d32f2f; }
    .field-errors { list-style: none; margin: 0; padding: 0; color: #d32f2f; }
    .banner { padding: 0.75rem 1rem; }
    .banner-success { background: #edf7ed; }
    .banner-error { background: #fdeded; }
  `,
})
export class ReactiveQuoteForComparisonComponent {
  private readonly http = inject(HttpClient);

  readonly bearerToken = signal('');
  readonly createdQuote = signal<CreatedQuote | null>(null);
  readonly submitting = signal(false);
  readonly formError = signal<string | null>(null);

  readonly quoteForm = new FormGroup({
    author: new FormControl('', { nonNullable: true, validators: [requiredTrimmed, Validators.maxLength(200)] }),
    text: new FormControl('', { nonNullable: true, validators: [requiredTrimmed, Validators.maxLength(1000)] }),
  });

  get authorControl() {
    return this.quoteForm.controls.author;
  }

  get textControl() {
    return this.quoteForm.controls.text;
  }

  private readonly authorInput = viewChild<ElementRef<HTMLInputElement>>('authorInput');
  private readonly textInput = viewChild<ElementRef<HTMLTextAreaElement>>('textInput');

  errorMessages(control: AbstractControl, label: string): string[] {
    if (!control.errors) {
      return [];
    }
    const messages: string[] = [];
    if (control.errors['required']) messages.push(`${label} is required.`);
    if (control.errors['maxlength']) {
      messages.push(`${label} must be ${control.errors['maxlength'].requiredLength} characters or fewer.`);
    }
    if (control.errors['server']) messages.push(control.errors['server']);
    return messages;
  }

  async onSubmit(): Promise<void> {
    this.createdQuote.set(null);
    this.formError.set(null);
    this.quoteForm.markAllAsTouched();

    if (this.quoteForm.invalid) {
      this.focusFirstInvalidField();
      return;
    }

    this.submitting.set(true);

    try {
      const created = await firstValueFrom(
        this.http.post<CreatedQuote>('/api/quotes', this.quoteForm.getRawValue(), {
          headers: this.bearerToken() ? { Authorization: `Bearer ${this.bearerToken()}` } : {},
        }),
      );
      this.createdQuote.set(created);
    } catch (err) {
      this.applySubmitError(err);
      this.focusFirstInvalidField();
    } finally {
      this.submitting.set(false);
    }
  }

  private applySubmitError(err: unknown): void {
    if (!(err instanceof HttpErrorResponse)) {
      this.formError.set('Could not create the quote.');
      return;
    }
    if (err.status === 400) {
      const problem = err.error as ValidationProblemDetails | null;
      if (problem?.errors) {
        for (const [key, messages] of Object.entries(problem.errors)) {
          const control = key === 'Author' ? this.authorControl : key === 'Text' ? this.textControl : null;
          if (control) {
            control.setErrors({ ...control.errors, server: messages.join(' ') });
          } else {
            this.formError.set(messages.join(' '));
          }
        }
        return;
      }
    }
    if (err.status === 401) {
      this.formError.set('Not authorized to create quotes. Provide a valid bearer token above.');
      return;
    }
    this.formError.set('Could not create the quote.');
  }

  private focusFirstInvalidField(): void {
    if (this.authorControl.invalid) {
      this.authorInput()?.nativeElement.focus();
    } else if (this.textControl.invalid) {
      this.textInput()?.nativeElement.focus();
    }
  }
}
