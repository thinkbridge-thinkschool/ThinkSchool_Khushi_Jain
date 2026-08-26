import { HttpClient, HttpErrorResponse, type HttpInterceptorFn } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Router, type CanActivateFn } from '@angular/router';
import { catchError, finalize, of, shareReplay, switchMap, tap, throwError, type Observable } from 'rxjs';
import { API_BASE_URL, TokenStore, type AccessTokenResponse } from './http';

/**
 * Everything about who is signed in. The access token itself lives in
 * TokenStore, which is all the transport layer needs to know about, so the
 * interceptors in http.ts stay independent of any of this.
 *
 * Nothing is written to localStorage: a reload signs you out, which the guard
 * turns into a redirect. A token in localStorage is readable by any script on
 * the page, and this session is cheap to recreate.
 */
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

  canRefresh(): boolean {
    return this.session.refreshToken() !== '';
  }

  /**
   * At most one refresh is ever in flight. The API rotates the refresh token on
   * every call and revokes the whole token family if a spent one is presented,
   * so two parallel refreshes would not just waste a call -- the second would
   * look like a replay and sign the user out completely.
   */
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

    // Cleared first so the UI is signed out even if the call fails. Logout
    // needs no bearer token and answers 204 for a token it does not know.
    this.session.clear();

    return refreshToken
      ? this.http.post(`${this.base}/api/auth/logout`, { refresh_token: refreshToken })
      : of(null);
  }
}

/**
 * Turns one 401 into a refresh plus one replay of the original request.
 *
 * It sits above the auth interceptor, so the request it holds carries no
 * Authorization header yet: replaying it through next() lets the auth
 * interceptor add the newly issued token. Auth endpoints are skipped, both to
 * avoid recursion and because their 401 is the answer, not a stale token.
 */
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
        // A refresh that fails has already cleared the session. The caller is
        // told about the original 401, not about the refresh attempt. This has
        // to catch before the replay, or a failure of the replayed request
        // would be reported as a 401 whatever it really was.
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
