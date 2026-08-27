import { HttpClient } from '@angular/common/http';
import { Component, ElementRef, inject, signal, viewChild } from '@angular/core';
import {
  AbstractControl,
  FormControl,
  FormGroup,
  ReactiveFormsModule,
  ValidationErrors,
  Validators,
} from '@angular/forms';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { API_BASE_URL, isAppError, toAppError, type CreatedQuote } from './http';
import { SessionStore } from './session';

// Validators.required only rejects an empty string; the API rejects
// whitespace-only too, since Quote.Create tests IsNullOrWhiteSpace.
function requiredTrimmed(control: AbstractControl): ValidationErrors | null {
  const value = control.value;

  return typeof value === 'string' && value.trim().length > 0 ? null : { required: true };
}

/**
 * Day 14 piece 1. Reactive Forms with the a11y wiring the piece asks for:
 * every input has an associated label, an invalid field carries aria-invalid
 * and points at its message list through aria-describedby, that list is
 * role="alert", and a rejected submit moves focus to the first invalid field.
 *
 * Field limits mirror the real POST /api/quotes contract -- author 200, text
 * 1000 -- and a 400 from the server is folded back onto the field it names.
 */
@Component({
  selector: 'app-reactive-form-page',
  imports: [ReactiveFormsModule],
  template: `
    <section class="card">
      <h2>Create a quote — Reactive Forms</h2>
      <p class="hint">
        FormGroup and FormControl, validators as functions, errors read off the control. The bearer
        token comes from the session, so this page needs you signed in.
      </p>

      @if (createdQuote(); as quote) {
        <p class="banner good" role="status">Quote #{{ quote.id }} created.</p>
      }

      <form [formGroup]="quoteForm" (ngSubmit)="onSubmit()" novalidate>
        <div class="field">
          <label for="rf-author">Author</label>
          <input
            id="rf-author"
            #authorInput
            type="text"
            formControlName="author"
            [attr.aria-invalid]="authorControl.touched && authorControl.invalid ? true : null"
            [attr.aria-describedby]="authorDescribedBy()"
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
            [attr.aria-describedby]="textDescribedBy()"
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

        <div class="row">
          <button type="submit" [disabled]="submitting()">
            @if (submitting()) {
              Creating…
            } @else {
              Create quote
            }
          </button>
        </div>

        @if (formError()) {
          <p class="banner bad" role="alert">{{ formError() }}</p>
        }
      </form>
    </section>
  `,
})
export class ReactiveFormPageComponent {
  private readonly http = inject(HttpClient);
  private readonly session = inject(SessionStore);
  private readonly router = inject(Router);
  private readonly quotes = `${inject(API_BASE_URL)}/api/quotes`;

  readonly createdQuote = signal<CreatedQuote | null>(null);
  readonly submitting = signal(false);
  readonly formError = signal<string | null>(null);

  readonly quoteForm = new FormGroup({
    author: new FormControl('', {
      nonNullable: true,
      validators: [requiredTrimmed, Validators.maxLength(200)],
    }),
    text: new FormControl('', {
      nonNullable: true,
      validators: [requiredTrimmed, Validators.maxLength(1000)],
    }),
  });

  get authorControl() {
    return this.quoteForm.controls.author;
  }

  get textControl() {
    return this.quoteForm.controls.text;
  }

  private readonly authorInput = viewChild<ElementRef<HTMLInputElement>>('authorInput');
  private readonly textInput = viewChild<ElementRef<HTMLTextAreaElement>>('textInput');

  authorDescribedBy(): string | null {
    return this.authorControl.touched && this.authorControl.invalid ? 'rf-author-error' : null;
  }

  textDescribedBy(): string | null {
    return this.textControl.touched && this.textControl.invalid ? 'rf-text-error' : null;
  }

  errorMessages(control: AbstractControl, label: string): string[] {
    if (!control.errors) {
      return [];
    }

    const messages: string[] = [];

    if (control.errors['required']) messages.push(`${label} is required.`);
    if (control.errors['maxlength']) {
      messages.push(
        `${label} must be ${control.errors['maxlength'].requiredLength} characters or fewer.`,
      );
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
        this.http.post<CreatedQuote>(this.quotes, this.quoteForm.getRawValue()),
      );
      this.createdQuote.set(created);
      this.quoteForm.reset();
    } catch (failure) {
      this.applySubmitError(failure);
      this.focusFirstInvalidField();
    } finally {
      this.submitting.set(false);
    }
  }

  /**
   * The error-mapping interceptor has already turned the response into an
   * AppError, so there is no ProblemDetails parsing left to do here: the field
   * errors arrive keyed by the C# property names the API used, Author and Text.
   */
  private applySubmitError(failure: unknown): void {
    const error = toAppError(failure);
    console.error('Create failed', error);

    if (!isAppError(failure)) {
      this.formError.set(error.message);
      return;
    }

    const entries = Object.entries(error.fieldErrors);

    if (entries.length > 0) {
      for (const [key, messages] of entries) {
        const control =
          key === 'Author' ? this.authorControl : key === 'Text' ? this.textControl : null;

        if (control) {
          control.setErrors({ ...control.errors, server: messages.join(' ') });
          control.markAsTouched();
        } else {
          this.formError.set(messages.join(' '));
        }
      }

      return;
    }

    this.formError.set(error.message);

    // A 401 that survived the refresh interceptor means the session is gone,
    // not that this one request was unlucky.
    if (error.kind === 'unauthorized' && !this.session.isSignedIn()) {
      this.router.navigate(['/login']);
    }
  }

  private focusFirstInvalidField(): void {
    if (this.authorControl.invalid) {
      this.authorInput()?.nativeElement.focus();
    } else if (this.textControl.invalid) {
      this.textInput()?.nativeElement.focus();
    }
  }
}
