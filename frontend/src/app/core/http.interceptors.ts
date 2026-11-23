import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, switchMap, throwError } from 'rxjs';
import { ApiProblem } from './models';
import { AuthStore } from './auth.store';
import { ToastService } from './toast.service';

const isAuthCall = (url: string) => url.includes('/api/v1/auth/');

/**
 * Functional interceptor (Angular 15+: no classes, no HTTP_INTERCEPTORS token).
 *  1. attaches the bearer token   2. on 401, refreshes ONCE and replays the original request.
 * If the refresh fails the user is genuinely logged out -> send them to /login.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthStore);
  const router = inject(Router);

  const withToken = (r: typeof req) => {
    const token = auth.accessToken();
    return token && !isAuthCall(r.url) ? r.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : r;
  };

  return next(withToken(req)).pipe(
    catchError((err: unknown) => {
      if (err instanceof HttpErrorResponse && err.status === 401 && !isAuthCall(req.url)) {
        return auth.refresh().pipe(
          switchMap((session) => {
            if (session) return next(withToken(req));          // retry with the new token
            router.navigate(['/login'], { queryParams: { returnUrl: router.url } });
            return throwError(() => err);
          }),
        );
      }
      return throwError(() => err);
    }),
  );
};

/** Global, user-facing handling of the errors a component cannot sensibly handle itself (network, 429, 5xx). 4xx business errors pass through. */
export const errorInterceptor: HttpInterceptorFn = (req, next) => {
  const toast = inject(ToastService);
  return next(req).pipe(
    catchError((err: unknown) => {
      if (err instanceof HttpErrorResponse) {
        if (err.status === 0) toast.error('Cannot reach the server. Check your connection.');
        else if (err.status === 429) toast.error(`Too many requests. Try again in ${err.headers.get('Retry-After') ?? 'a few'} seconds.`);
        else if (err.status >= 500) toast.error(`Something went wrong on our side. Reference: ${err.headers.get('X-Correlation-Id') ?? 'n/a'}`);
      }
      return throwError(() => err);
    }),
  );
};

/** Extracts the API's problem+json (or a safe fallback) from whatever was thrown. */
export function problemOf(err: unknown): ApiProblem {
  if (err instanceof HttpErrorResponse && err.error && typeof err.error === 'object' && 'code' in err.error) return err.error as ApiProblem;
  return { status: err instanceof HttpErrorResponse ? err.status : 0, title: 'Unexpected error', code: 'unknown' };
}
