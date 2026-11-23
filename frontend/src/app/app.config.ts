import { ApplicationConfig, inject, provideAppInitializer, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { routes } from './app.routes';
import { AuthStore } from './core/auth.store';
import { authInterceptor, errorInterceptor } from './core/http.interceptors';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes, withComponentInputBinding()),
    // Interceptor ORDER matters: the first one is the OUTERMOST. errorInterceptor wraps authInterceptor, so it only sees a failure after auth already tried (and retried) it.
    provideHttpClient(withFetch(), withInterceptors([errorInterceptor, authInterceptor])),
    // Before the first route renders, try to restore the session from the httpOnly refresh cookie (a reload would otherwise log you out).
    provideAppInitializer(() => firstValueFrom(inject(AuthStore).restore())),
  ],
};

