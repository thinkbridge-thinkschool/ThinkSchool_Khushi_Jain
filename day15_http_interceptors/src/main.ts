import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { bootstrapApplication } from '@angular/platform-browser';
import { provideRouter, type Routes } from '@angular/router';
import { AppComponent } from './app';
import { authInterceptor, errorMappingInterceptor, retryInterceptor } from './http';
import { LoginPageComponent } from './login-page';
import { QuotesPageComponent } from './quotes-page';
import { authGuard, refreshInterceptor } from './session';

const routes: Routes = [
  { path: 'login', component: LoginPageComponent },
  { path: 'quotes', component: QuotesPageComponent, canActivate: [authGuard] },
  { path: '', pathMatch: 'full', redirectTo: 'quotes' },
  { path: '**', redirectTo: 'quotes' },
];

// Order is outermost first, and all four positions matter. Error mapping wraps
// everything, so it maps only what survives. Refresh sits above auth, so the
// request it replays has no Authorization header yet and picks up the new token
// on the way down. Retry is innermost, closest to the wire.
bootstrapApplication(AppComponent, {
  providers: [
    provideRouter(routes),
    provideHttpClient(
      withInterceptors([
        errorMappingInterceptor,
        refreshInterceptor,
        authInterceptor,
        retryInterceptor,
      ]),
    ),
  ],
}).catch((error) => console.error(error));
