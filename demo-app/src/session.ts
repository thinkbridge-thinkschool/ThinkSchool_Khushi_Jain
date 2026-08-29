import { HttpClient, HttpErrorResponse, type HttpInterceptorFn } from '@angular/common/http';
import { Injectable, InjectionToken, computed, inject, signal } from '@angular/core';
import { Router, type CanActivateFn } from '@angular/router';
import { catchError, finalize, of, shareReplay, switchMap, tap, throwError, type Observable } from 'rxjs';
import { API_BASE_URL, TokenStore, type AccessTokenResponse } from './http';

export interface DevCredentials {
  email: string;
  password: string;
}

export const DEV_CREDENTIALS = new InjectionToken<DevCredentials | null>('DEV_CREDENTIALS', {
  providedIn: 'root',
  factory: () => null,
});

@Injectable({ providedIn: 'root' })
export class SessionStore {
  private readonly tokens = inject(TokenStore);

  readonly email = signal('');
  readonly refreshToken = signal('');
  readonly expiresAt = signal(0);

  readonly isSignedIn = computed(() => this.tokens.token() !== '');

  store(granted: AccessTokenResponse, email: string): void {
    this.tokens.token.set(granted.access_token);
    this.refreshToken.set(granted.refresh_token);
    this.expiresAt.set(Date.now() + granted.expires_in * 1000);
    this.email.set(email);
  }

  clear(): void {
    this.tokens.token.set('');
    this.refreshToken.set('');
    this.expiresAt.set(0);
    this.email.set('');
  }
}

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly session = inject(SessionStore);
  private readonly base = inject(API_BASE_URL);

  private inFlightRefresh: Observable<AccessTokenResponse> | null = null;

  signIn(email: string, password: string): Observable<AccessTokenResponse> {
    return this.http
      .post<AccessTokenResponse>(`${this.base}/api/auth/login`, { email, password })
      .pipe(tap((granted) => this.session.store(granted, email)));
  }

  register(email: string, password: string): Observable<AccessTokenResponse> {
    return this.http
      .post<AccessTokenResponse>(`${this.base}/api/auth/register`, { email, password })
      .pipe(tap((granted) => this.session.store(granted, email)));
  }

  canRefresh(): boolean {
    return this.session.refreshToken() !== '';
  }

  refresh(): Observable<AccessTokenResponse> {
    if (this.inFlightRefresh) {
      return this.inFlightRefresh;
    }

    this.inFlightRefresh = this.http
      .post<AccessTokenResponse>(`${this.base}/api/auth/refresh`, {
        refresh_token: this.session.refreshToken(),
      })
      .pipe(
        tap((granted) => this.session.store(granted, this.session.email())),
        catchError((error: unknown) => {
          this.session.clear();

          return throwError(() => error);
        }),
        finalize(() => (this.inFlightRefresh = null)),
        shareReplay({ bufferSize: 1, refCount: false }),
      );

    return this.inFlightRefresh;
  }

  signOut(): Observable<unknown> {
    const refreshToken = this.session.refreshToken();

    this.session.clear();

    return refreshToken
      ? this.http.post(`${this.base}/api/auth/logout`, { refresh_token: refreshToken })
      : of(null);
  }
}

export const refreshInterceptor: HttpInterceptorFn = (request, next) => {
  const auth = inject(AuthService);
  const base = inject(API_BASE_URL);

  if (request.url.startsWith(`${base}/api/auth/`)) {
    return next(request);
  }

  return next(request).pipe(
    catchError((error: unknown) => {
      if (!(error instanceof HttpErrorResponse) || error.status !== 401 || !auth.canRefresh()) {
        return throwError(() => error);
      }

      return auth.refresh().pipe(
        catchError(() => throwError(() => error)),
        switchMap(() => next(request)),
      );
    }),
  );
};

export const authGuard: CanActivateFn = (_route, state) => {
  const session = inject(SessionStore);

  if (session.isSignedIn()) {
    return true;
  }

  return inject(Router).createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
};
