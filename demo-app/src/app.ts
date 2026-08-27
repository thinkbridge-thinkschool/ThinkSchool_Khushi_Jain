import { Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { SessionStore } from './session';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    <div class="shell">
      <header class="masthead">
        <div>
          <h1>Quotes</h1>
          <p>Angular 21, signals and zoneless, over an ASP.NET Core 10 minimal API.</p>
        </div>

        @if (session.isSignedIn()) {
          <span class="badge">signed in as {{ session.email() }}</span>
        }
      </header>

      <nav class="tabs" aria-label="Sections">
        <a routerLink="/quotes" routerLinkActive="on">Quotes</a>
        <a routerLink="/list-detail" routerLinkActive="on">List &amp; detail</a>
        <a routerLink="/create" routerLinkActive="on">Create a quote</a>
        <a routerLink="/create-signal" routerLinkActive="on">Signal Forms</a>
        <a routerLink="/http" routerLinkActive="on">HTTP layer</a>
        <a routerLink="/routing" routerLinkActive="on">Routing</a>
        <a routerLink="/collection" routerLinkActive="on">Collection</a>
      </nav>

      @if (!session.isSignedIn()) {
        <p class="banner bad" role="alert">
          Not signed in, so the pages that write are locked. Copy
          <code>public/dev-session.example.json</code> to
          <code>public/dev-session.json</code>, fill in the seeded account, and reload.
        </p>
      }

      <router-outlet />
    </div>
  `,
})
export class AppComponent {
  readonly session = inject(SessionStore);
}
