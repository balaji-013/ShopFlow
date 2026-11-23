import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthStore } from './core/auth.store';
import { CartStore } from './features/cart/cart.store';
import { RealtimeService } from './core/realtime.service';
import { ToastHost } from './shared/toast-host';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, ToastHost],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="nav">
      <a routerLink="/" class="brand">ShopFlow</a>
      @if (auth.isAuthenticated()) {
        <nav>
          <a routerLink="/shop" routerLinkActive="active">Shop</a>
          <a routerLink="/cart" routerLinkActive="active">Cart @if (cart.count() > 0) { <span class="pill">{{ cart.count() }}</span> }</a>
          <a routerLink="/orders" routerLinkActive="active">Orders</a>
          @if (auth.isAdmin()) {
            <a routerLink="/admin/products" routerLinkActive="active">Products</a>
            <a routerLink="/admin/report" routerLinkActive="active">Report</a>
          }
        </nav>
        <div class="user">{{ auth.user()?.email }} <button class="btn sm" (click)="logout()">Sign out</button></div>
      } @else {
        <nav><a routerLink="/login">Sign in</a><a routerLink="/register">Register</a></nav>
      }
    </header>
    <main class="container"><router-outlet /></main>
    <app-toast-host />`,
})
export class App {
  protected readonly auth = inject(AuthStore);
  protected readonly cart = inject(CartStore);
  private readonly router = inject(Router);
  private readonly realtime = inject(RealtimeService);   // instantiate once at the shell: the connection follows login/logout for the whole app

  protected logout() { this.auth.logout().subscribe(() => this.router.navigateByUrl('/login')); }
}

