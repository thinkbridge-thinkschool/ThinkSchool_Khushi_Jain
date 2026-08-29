import type { Routes } from '@angular/router';
import { CollectionPageComponent } from './collection-page';
import { ListDetailPageComponent } from './list-detail-page';
import { LoginPageComponent } from './login-page';
import { QuotesPageComponent } from './quotes-page';
import { ReactiveFormPageComponent } from './reactive-form-page';
import { RoutedListPageComponent } from './routed-list-page';
import { authGuard } from './session';
import { SignalFormPageComponent } from './signal-form-page';
import { SignalsPageComponent } from './signals-page';

export const routes: Routes = [
  { path: 'quotes', component: SignalsPageComponent, canActivate: [authGuard] },
  { path: 'list-detail', component: ListDetailPageComponent, canActivate: [authGuard] },
  { path: 'create', component: ReactiveFormPageComponent, canActivate: [authGuard] },
  { path: 'create-signal', component: SignalFormPageComponent, canActivate: [authGuard] },
  { path: 'http', component: QuotesPageComponent, canActivate: [authGuard] },

  {
    path: 'routing',
    canActivate: [authGuard],
    children: [
      { path: '', component: RoutedListPageComponent },

      {
        path: ':id',
        loadComponent: () =>
          import('./routed-detail-page').then((module) => module.RoutedDetailPageComponent),
      },
    ],
  },

  { path: 'collection', component: CollectionPageComponent, canActivate: [authGuard] },

  { path: 'login', component: LoginPageComponent },
  { path: '', pathMatch: 'full', redirectTo: 'quotes' },
  { path: '**', redirectTo: 'quotes' },
];
