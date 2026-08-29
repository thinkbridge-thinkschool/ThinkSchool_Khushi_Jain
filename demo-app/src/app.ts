import { Component, inject } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthService, SessionStore } from './session';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    <div class="shell">
      <header class="masthead">
        <div>
          <h1>Quotes</h1>
        </div>

        @if (session.isSignedIn()) {
          <div class="session">
            <span class="badge">signed in as {{ session.email() }}</span>
            <button class="quiet" type="button" (click)="signOut()">Sign out</button>
          </div>
        }
      </header>

      @if (session.isSignedIn()) {
        <nav class="tabs" aria-label="Sections">
          <a routerLink="/quotes" routerLinkActive="on">Quotes</a>
          <a routerLink="/list-detail" routerLinkActive="on">List &amp; detail</a>
          <a routerLink="/create" routerLinkActive="on">Create a quote</a>
          <a routerLink="/create-signal" routerLinkActive="on">Signal Forms</a>
          <a routerLink="/http" routerLinkActive="on">HTTP layer</a>
          <a routerLink="/routing" routerLinkActive="on">Routing</a>
          <a routerLink="/collection" routerLinkActive="on">Collection</a>
        </nav>
      }

      <router-outlet />
    </div>
  `,
})
export class AppComponent {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  readonly session = inject(SessionStore);

  signOut(): void {
    this.auth.signOut().subscribe({
      next: () => this.router.navigateByUrl('/login'),
      error: () => this.router.navigateByUrl('/login'),
    });
  }
}
