import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthStore } from './auth.store';

/** Route guards are UX only - they hide screens. REAL security is the server (JWT validation + role policies + tenant filters). */
export const authGuard: CanActivateFn = (_route, state) => {
  const auth = inject(AuthStore);
  return auth.isAuthenticated() ? true : inject(Router).createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
};

export const adminGuard: CanActivateFn = () => {
  const auth = inject(AuthStore);
  return auth.isAdmin() ? true : inject(Router).createUrlTree(['/']);
};

export const guestGuard: CanActivateFn = () => (inject(AuthStore).isAuthenticated() ? inject(Router).createUrlTree(['/']) : true);
