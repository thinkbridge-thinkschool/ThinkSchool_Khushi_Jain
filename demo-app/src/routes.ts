import type { Routes } from '@angular/router';
import { ListDetailPageComponent } from './list-detail-page';
import { LoginPageComponent } from './login-page';
import { QuotesPageComponent } from './quotes-page';
import { ReactiveFormPageComponent } from './reactive-form-page';
import { RoutedListPageComponent } from './routed-list-page';
import { authGuard } from './session';
import { SignalFormPageComponent } from './signal-form-page';
import { SignalsPageComponent } from './signals-page';

/**
 * Declared here rather than in main.ts so a test can import the real table
 * without bootstrapping the application.
 *
 * /login is deliberately absent from the nav. The session is granted before the
 * app starts, so the page is only reachable by URL, as a fallback for when that
 * fails or the session is left to expire.
 */
export const routes: Routes = [
  { path: 'quotes', component: SignalsPageComponent },
  { path: 'list-detail', component: ListDetailPageComponent },
  { path: 'create', component: ReactiveFormPageComponent, canActivate: [authGuard] },
  { path: 'create-signal', component: SignalFormPageComponent, canActivate: [authGuard] },
  { path: 'http', component: QuotesPageComponent, canActivate: [authGuard] },

  // Day 16. The guard sits on the parent, so one canActivate covers the list
  // and the detail, and it runs before the child route is matched -- a
  // signed-out visitor never downloads the detail chunk.
  {
    path: 'routing',
    canActivate: [authGuard],
    children: [
      { path: '', component: RoutedListPageComponent },

      // The only reference to the detail component anywhere in the app. A
      // static import of it would put it in the initial bundle and quietly
      // undo the lazy loading, which is why this names it as a string.
      {
        path: ':id',
        loadComponent: () =>
          import('./routed-detail-page').then((module) => module.RoutedDetailPageComponent),
      },
    ],
  },

  { path: 'login', component: LoginPageComponent },
  { path: '', pathMatch: 'full', redirectTo: 'quotes' },
  { path: '**', redirectTo: 'quotes' },
];
