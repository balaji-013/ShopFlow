import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { AuthStore } from './auth.store';
import { authInterceptor } from './http.interceptors';
import { Session } from './models';

const session = (token: string): Session => ({ accessToken: token, expiresIn: 900, user: { id: 'u1', email: 'a@b.c', fullName: 'A', role: 'Customer' } });

describe('authInterceptor', () => {
  let http: HttpClient; let ctrl: HttpTestingController; let auth: AuthStore;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideRouter([{ path: 'login', children: [] }]), provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()] });
    http = TestBed.inject(HttpClient); ctrl = TestBed.inject(HttpTestingController); auth = TestBed.inject(AuthStore);
  });
  afterEach(() => ctrl.verify());

  it('attaches the bearer token to API calls but not to auth calls', () => {
    auth.login('a@b.c', 'pw').subscribe();
    const login = ctrl.expectOne('/api/v1/auth/login');
    expect(login.request.headers.has('Authorization')).toBe(false);
    login.flush(session('T1'));

    http.get('/api/v1/products').subscribe();
    expect(ctrl.expectOne('/api/v1/products').request.headers.get('Authorization')).toBe('Bearer T1');
  });

  it('SINGLE-FLIGHT: three parallel 401s trigger exactly ONE refresh, then every request is replayed with the new token', () => {
    auth.login('a@b.c', 'pw').subscribe(); ctrl.expectOne('/api/v1/auth/login').flush(session('OLD'));

    const results: unknown[] = [];
    for (const url of ['/api/v1/a', '/api/v1/b', '/api/v1/c']) http.get(url).subscribe((r) => results.push(r));

    // all three expire at once
    for (const url of ['/api/v1/a', '/api/v1/b', '/api/v1/c']) ctrl.expectOne(url).flush({}, { status: 401, statusText: 'Unauthorized' });

    const refresh = ctrl.expectOne('/api/v1/auth/refresh');      // expectOne => fails if there were 2 or 3 refresh calls
    refresh.flush(session('NEW'));

    for (const url of ['/api/v1/a', '/api/v1/b', '/api/v1/c']) {
      const retry = ctrl.expectOne(url);
      expect(retry.request.headers.get('Authorization')).toBe('Bearer NEW');
      retry.flush({ ok: url });
    }
    expect(results).toHaveLength(3);
  });

  it('when the refresh itself fails the user is logged out and the original error surfaces', () => {
    auth.login('a@b.c', 'pw').subscribe(); ctrl.expectOne('/api/v1/auth/login').flush(session('OLD'));
    let failed = false;
    http.get('/api/v1/x').subscribe({ error: () => (failed = true) });
    ctrl.expectOne('/api/v1/x').flush({}, { status: 401, statusText: 'Unauthorized' });
    ctrl.expectOne('/api/v1/auth/refresh').flush({}, { status: 401, statusText: 'Unauthorized' });
    expect(failed).toBe(true);
    expect(auth.isAuthenticated()).toBe(false);
  });
});

