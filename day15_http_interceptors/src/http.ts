import { HttpErrorResponse, type HttpInterceptorFn } from '@angular/common/http';
import { InjectionToken, Injectable, inject, signal } from '@angular/core';
import { catchError, retry, throwError, timer } from 'rxjs';

// Empty by default: the dev server proxies /api to the API, so the browser
// asks its own origin. Anything that talks to the API by absolute URL has to
// override this, or the auth interceptor will not recognise the URL as ours.
export const API_BASE_URL = new InjectionToken<string>('API_BASE_URL', {
  providedIn: 'root',
  factory: () => '',
});

// One item of GET /api/quotes' `items` array, as the running API returns it.
export interface QuoteSummary {
  id: number;
  author: string;
  text: string;
  ownerId: string | null;
  ownerActive: boolean;
}

// The envelope GET /api/quotes returns. `total` is the whole collection, not the page.
export interface QuotesPage {
  page: number;
  size: number;
  total: number;
  items: QuoteSummary[];
}

// The 200 body of POST /api/auth/login and POST /api/auth/refresh. Refresh
// rotates both tokens, and takes its argument as snake_case `refresh_token`.
export interface AccessTokenResponse {
  access_token: string;
  refresh_token: string;
  expires_in: number;
}

// The 201 body of POST /api/quotes. It carries isDeleted, and has no ownerActive.
export interface CreatedQuote {
  id: number;
  author: string;
  text: string;
  ownerId: string | null;
  isDeleted: boolean;
}

export type AppErrorKind =
  | 'network'
  | 'validation'
  | 'unauthorized'
  | 'forbidden'
  | 'not-found'
  | 'busy'
  | 'server'
  | 'unknown';

export interface AppError {
  readonly appError: true;
  kind: AppErrorKind;
  status: number;
  // Safe to render. Never carries a `type` URI, a stack, or a server-side title.
  message: string;
  // ValidationProblemDetails.errors when the response had one, otherwise empty.
  // Keys are whatever the server used: `page/size` on the list endpoint,
  // `Author`/`Text` on the create endpoint.
  fieldErrors: Record<string, string[]>;
  // Correlates with the TraceId on the API's Serilog lines for the same request.
  traceId: string | null;
  url: string | null;
}

// The API answers 401 and 404 with a zero-length body, and answers a network
// failure with a ProgressEvent, so an error body cannot be assumed to be a
// problem document at all.
interface ProblemDetails {
  title?: string;
  status?: number;
  detail?: string;
  traceId?: string;
  errors?: Record<string, string[]>;
}

function readProblemDetails(body: unknown): ProblemDetails | null {
  if (typeof body !== 'object' || body === null || Array.isArray(body)) {
    return null;
  }

  if (typeof ProgressEvent !== 'undefined' && body instanceof ProgressEvent) {
    return null;
  }

  return body as ProblemDetails;
}

function readFieldErrors(problem: ProblemDetails | null): Record<string, string[]> {
  const errors = problem?.errors;

  if (typeof errors !== 'object' || errors === null) {
    return {};
  }

  const validated: Record<string, string[]> = {};

  for (const [key, messages] of Object.entries(errors)) {
    if (Array.isArray(messages)) {
      validated[key] = messages.filter((message): message is string => typeof message === 'string');
    }
  }

  return validated;
}

export function isAppError(value: unknown): value is AppError {
  return typeof value === 'object' && value !== null && (value as AppError).appError === true;
}

export function toAppError(error: unknown): AppError {
  if (isAppError(error)) {
    return error;
  }

  if (!(error instanceof HttpErrorResponse)) {
    return {
      appError: true,
      kind: 'unknown',
      status: 0,
      message: 'Something went wrong. Please try again.',
      fieldErrors: {},
      traceId: null,
      url: null,
    };
  }

  const problem = readProblemDetails(error.error);
  const fieldErrors = readFieldErrors(problem);
  const base = {
    appError: true as const,
    status: error.status,
    fieldErrors,
    traceId: typeof problem?.traceId === 'string' ? problem.traceId : null,
    url: error.url,
  };

  // status 0 is a request that never got an HTTP answer: API down, DNS, CORS.
  if (error.status === 0) {
    return {
      ...base,
      kind: 'network',
      message: 'Could not reach the quotes service. Check that it is running, then try again.',
    };
  }

  if (error.status === 400 || error.status === 422) {
    const listed = Object.values(fieldErrors).flat();

    return {
      ...base,
      kind: 'validation',
      message:
        listed.length > 0
          ? listed.join(' ')
          : typeof problem?.detail === 'string' && problem.detail.length > 0
            ? problem.detail
            : 'Some of those details were not accepted.',
    };
  }

  if (error.status === 401) {
    return { ...base, kind: 'unauthorized', message: 'You need to be signed in to do that.' };
  }

  if (error.status === 403) {
    return { ...base, kind: 'forbidden', message: 'You do not have permission to do that.' };
  }

  if (error.status === 404) {
    return { ...base, kind: 'not-found', message: 'That was not found.' };
  }

  if (error.status === 408 || error.status === 429) {
    return { ...base, kind: 'busy', message: 'The quotes service is busy. Please try again in a moment.' };
  }

  if (error.status >= 500) {
    return {
      ...base,
      kind: 'server',
      message: 'The quotes service is having trouble. Please try again shortly.',
    };
  }

  return { ...base, kind: 'unknown', message: 'That request could not be completed.' };
}

@Injectable({ providedIn: 'root' })
export class TokenStore {
  readonly token = signal('');
}

@Injectable({ providedIn: 'root' })
export class RequestLog {
  readonly lines = signal<string[]>([]);

  record(line: string): void {
    this.lines.update((lines) => [...lines, line]);
  }
}

export const authInterceptor: HttpInterceptorFn = (request, next) => {
  const token = inject(TokenStore).token().trim();
  const apiPrefix = `${inject(API_BASE_URL)}/api/`;

  // Only this API's own URLs get the token, and a header already on the
  // request wins, so a caller can override it per call.
  if (!token || !request.url.startsWith(apiPrefix) || request.headers.has('Authorization')) {
    return next(request);
  }

  return next(request.clone({ setHeaders: { Authorization: `Bearer ${token}` } }));
};

const RETRYABLE_STATUSES = new Set([0, 408, 429, 500, 502, 503, 504]);
const MAX_RETRIES = 2;
const BASE_DELAY_MS = 300;

export const retryInterceptor: HttpInterceptorFn = (request, next) => {
  // Only methods the API defines as idempotent are replayed. A failed POST may
  // have been applied on the server before the failure reached us.
  if (request.method !== 'GET' && request.method !== 'HEAD') {
    return next(request);
  }

  const log = inject(RequestLog);

  return next(request).pipe(
    retry({
      count: MAX_RETRIES,
      delay: (error, attempt) => {
        if (!(error instanceof HttpErrorResponse) || !RETRYABLE_STATUSES.has(error.status)) {
          return throwError(() => error);
        }

        const wait = BASE_DELAY_MS * 2 ** (attempt - 1);
        log.record(
          `${request.method} ${request.urlWithParams} failed with ${error.status} — ` +
            `retry ${attempt} of ${MAX_RETRIES} in ${wait}ms`,
        );

        return timer(wait);
      },
    }),
  );
};

export const errorMappingInterceptor: HttpInterceptorFn = (request, next) =>
  next(request).pipe(catchError((error: unknown) => throwError(() => toAppError(error))));
