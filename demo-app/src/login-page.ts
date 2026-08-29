import { Component, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { toAppError } from './http';
import { AuthService, DEV_CREDENTIALS } from './session';

@Component({
  selector: 'app-login-page',
  template: `
    <section class="card centre">
      <h2>{{ signingUp() ? 'Create an account' : 'Sign in' }}</h2>

      @if (signingUp()) {
        <p class="hint">Pick any email and a password of at least 8 characters.</p>
      } @else if (prefilled) {
        <p class="hint">
          Filled in from <code>public/dev-session.json</code>. Sign in to reach the tabs.
        </p>
      } @else {
        <p class="hint">Sign in to reach the tabs.</p>
      }

      <form (submit)="submit($event)" novalidate>
        <div class="field">
          <label for="email">Email</label>
          <input
            id="email"
            type="email"
            autocomplete="username"
            [value]="email()"
            (input)="email.set($any($event.target).value)"
          />
        </div>

        <div class="field">
          <label for="password">Password</label>
          <input
            id="password"
            type="password"
            [attr.autocomplete]="signingUp() ? 'new-password' : 'current-password'"
            [value]="password()"
            (input)="password.set($any($event.target).value)"
          />
        </div>

        <div class="row">
          <button type="submit" [disabled]="busy()">
            @if (busy()) {
              {{ signingUp() ? 'Creating…' : 'Signing in…' }}
            } @else {
              {{ signingUp() ? 'Create account' : 'Sign in' }}
            }
          </button>
        </div>

        @if (message()) {
          <p class="banner bad" role="alert">{{ message() }}</p>
        }
      </form>

      <p class="hint">
        @if (signingUp()) {
          Already have an account?
          <button class="link" type="button" (click)="switchTo(false)">Sign in instead</button>
        } @else {
          No account yet?
          <button class="link" type="button" (click)="switchTo(true)">Create one</button>
        }
      </p>
    </section>
  `,
})
export class LoginPageComponent {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly devCredentials = inject(DEV_CREDENTIALS);

  readonly prefilled = this.devCredentials !== null;

  readonly signingUp = signal(false);
  readonly email = signal(this.devCredentials?.email ?? '');
  readonly password = signal(this.devCredentials?.password ?? '');
  readonly busy = signal(false);
  readonly message = signal('');

  switchTo(signingUp: boolean): void {
    this.signingUp.set(signingUp);
    this.message.set('');

    if (signingUp) {
      this.email.set('');
      this.password.set('');
    }
  }

  submit(event: Event): void {
    event.preventDefault();
    this.busy.set(true);
    this.message.set('');

    const signingUp = this.signingUp();
    const request = signingUp
      ? this.auth.register(this.email(), this.password())
      : this.auth.signIn(this.email(), this.password());

    request.subscribe({
      next: () => {
        this.password.set('');
        this.busy.set(false);
        this.router.navigateByUrl(this.route.snapshot.queryParamMap.get('returnUrl') ?? '/quotes');
      },
      error: (failure: unknown) => {
        const error = toAppError(failure);
        console.error(signingUp ? 'Sign-up failed' : 'Sign-in failed', error);
        this.message.set(this.explain(error.status, error.message, signingUp));
        this.busy.set(false);
      },
    });
  }

  private explain(status: number, fallback: string, signingUp: boolean): string {
    if (status === 409) {
      return 'That email is already registered. Sign in instead.';
    }

    if (status === 401 && !signingUp) {
      return 'That email and password were not accepted.';
    }

    if (status === 400 && signingUp) {
      return 'Enter a valid email and a password of at least 8 characters.';
    }

    return fallback;
  }
}
