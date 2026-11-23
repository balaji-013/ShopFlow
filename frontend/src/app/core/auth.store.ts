import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable, catchError, finalize, of, shareReplay, tap } from 'rxjs';
import { Session } from './models';

/**
 * Where does the JWT live? In MEMORY (a signal) - not localStorage.
 *  - localStorage is readable by any script on the page, so one XSS = stolen token.
 *  - The long-lived REFRESH token is an httpOnly cookie the browser sends by itself; JavaScript cannot read it.
 *  - A page reload loses the in-memory access token; the app silently gets a new one via the cookie (see provideAppInitializer).
 */
@Injectable({ providedIn: 'root' })
export class AuthStore {
  private readonly http = inject(HttpClient);
  private readonly session = signal<Session | null>(null);

  readonly user = computed(() => this.session()?.user ?? null);
  readonly isAuthenticated = computed(() => this.session() !== null);
  readonly isAdmin = computed(() => this.user()?.role === 'Admin');
  readonly accessToken = computed(() => this.session()?.accessToken ?? null);

  private refreshInFlight$: Observable<Session | null> | null = null;

  login(email: string, password: string) {
    return this.http.post<Session>('/api/v1/auth/login', { email, password }).pipe(tap((s) => this.session.set(s)));
  }

  register(v: { email: string; password: string; fullName: string; tenantCode: string }) {
    return this.http.post<Session>('/api/v1/auth/register', v).pipe(tap((s) => this.session.set(s)));
  }

  logout() {
    return this.http.post<void>('/api/v1/auth/logout', {}).pipe(
      catchError(() => of(void 0)),
      finalize(() => this.session.set(null)),
    );
  }

  /**
   * SINGLE-FLIGHT refresh. Five requests can hit 401 at the same instant when the access token expires. Because refresh tokens ROTATE
   * (each is single-use), five parallel refresh calls would make four of them fail - and the API would see that as token theft.
   * So all callers share ONE in-flight request (shareReplay) and everybody gets the same new token.
   */
  refresh(): Observable<Session | null> {
    if (!this.refreshInFlight$) {
      this.refreshInFlight$ = this.http.post<Session>('/api/v1/auth/refresh', {}).pipe(
        tap((s) => this.session.set(s)),
        catchError(() => { this.session.set(null); return of(null); }),
        finalize(() => (this.refreshInFlight$ = null)),
        shareReplay({ bufferSize: 1, refCount: false }),
      );
    }
    return this.refreshInFlight$;
  }

  /** Called once at startup: "am I still logged in?" */
  restore() { return this.refresh(); }
}
