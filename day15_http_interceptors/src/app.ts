import { Component, inject } from '@angular/core';
import { Router, RouterOutlet } from '@angular/router';
import { AuthService, SessionStore } from './session';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet],
  template: `
    <div class="shell">
      <header class="masthead">
        <div>
          <h1>Quotes</h1>
          <p>HttpClient with auth, refresh, retry and error-mapping interceptors.</p>
        </div>

        @if (session.isSignedIn()) {
          <div class="who">
            <span>{{ session.email() }} · token expires {{ expiry() }}</span>
            <button class="quiet" (click)="signOut()">Sign out</button>
          </div>
        }
      </header>

      <router-outlet />
    </div>
  `,
})
export class AppComponent {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  readonly session = inject(SessionStore);

  expiry(): string {
    const minutes = Math.round((this.session.expiresAt() - Date.now()) / 60_000);

    return minutes > 0 ? `in ${minutes} min` : 'now';
  }

  signOut(): void {
    this.auth.signOut().subscribe({
      next: () => this.router.navigate(['/login']),
      error: () => this.router.navigate(['/login']),
    });
  }
}
