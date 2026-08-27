import { Component, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { toAppError } from './http';
import { AuthService } from './session';

@Component({
  selector: 'app-login-page',
  template: `
    <section class="card centre">
      <h2>Sign in</h2>
      <p class="hint">The seeded development account, from Seed:AdminEmail and Seed:AdminPassword.</p>

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
            autocomplete="current-password"
            [value]="password()"
            (input)="password.set($any($event.target).value)"
          />
        </div>

        <div class="row">
          <button type="submit" [disabled]="busy()">
            @if (busy()) {
              Signing in…
            } @else {
              Sign in
            }
          </button>
        </div>

        @if (message()) {
          <p class="banner bad" role="alert">{{ message() }}</p>
        }
      </form>
    </section>
  `,
})
export class LoginPageComponent {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  readonly email = signal('');
  readonly password = signal('');
  readonly busy = signal(false);
  readonly message = signal('');

  submit(event: Event): void {
    event.preventDefault();
    this.busy.set(true);
    this.message.set('');

    this.auth.signIn(this.email(), this.password()).subscribe({
      next: () => {
        this.password.set('');
        this.busy.set(false);
        this.router.navigateByUrl(this.route.snapshot.queryParamMap.get('returnUrl') ?? '/quotes');
      },
      error: (failure: unknown) => {
        const error = toAppError(failure);
        console.error('Sign-in failed', error);

        // The API answers wrong credentials with a bodiless 401, the same
        // status a stale token gets, so the generic message would be wrong here.
        this.message.set(
          error.status === 401 ? 'That email and password were not accepted.' : error.message,
        );
        this.busy.set(false);
      },
    });
  }
}
