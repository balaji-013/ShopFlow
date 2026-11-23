import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { CurrencyPipe } from '@angular/common';
import { Router, RouterLink } from '@angular/router';
import { ApiService } from '../../core/api.service';
import { problemOf } from '../../core/http.interceptors';
import { ToastService } from '../../core/toast.service';
import { CartStore } from './cart.store';

@Component({
  selector: 'app-cart',
  imports: [CurrencyPipe, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h1>Your cart</h1>
    @if (cart.lines().length === 0) {
      <p class="muted">Your cart is empty. <a routerLink="/shop">Browse products</a></p>
    } @else {
      <table class="table">
        <thead><tr><th>Product</th><th>Price</th><th>Qty</th><th>Subtotal</th><th></th></tr></thead>
        <tbody>
          @for (l of cart.lines(); track l.productId) {
            <tr>
              <td>{{ l.name }}</td>
              <td>{{ l.unitPrice | currency }}</td>
              <td>
                <button class="btn sm" (click)="cart.setQuantity(l.productId, l.quantity - 1)" aria-label="Decrease">−</button>
                <span class="qty">{{ l.quantity }}</span>
                <button class="btn sm" (click)="cart.setQuantity(l.productId, l.quantity + 1)" aria-label="Increase">+</button>
              </td>
              <td>{{ l.unitPrice * l.quantity | currency }}</td>
              <td><button class="btn sm danger" (click)="cart.remove(l.productId)">Remove</button></td>
            </tr>
          }
        </tbody>
        <tfoot><tr><th colspan="3">Total (the server recalculates it from current prices)</th><th>{{ cart.total() | currency }}</th><th></th></tr></tfoot>
      </table>
      <button class="btn primary" (click)="checkout()" [disabled]="busy()">{{ busy() ? 'Placing order…' : 'Place order' }}</button>
    }`,
})
export class CartPage {
  protected readonly cart = inject(CartStore);
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);
  protected readonly busy = signal(false);

  protected checkout() {
    if (this.busy()) return;                         // first line of defence against double-click; the Idempotency-Key is the real one
    this.busy.set(true);
    this.api.placeOrder(this.cart.toRequestItems(), this.cart.idempotencyKey()).subscribe({
      next: () => { this.cart.clear(); this.toast.success('Order placed!'); this.router.navigateByUrl('/orders'); },
      error: (e) => {
        this.busy.set(false);
        const p = problemOf(e);
        if (p.code === 'insufficient_stock') this.toast.error('Sorry - one of the items just sold out or has too little stock. Adjust your cart.');
        else if (p.status < 500 && p.status !== 0 && p.status !== 429) this.toast.error(p.title);
      },
    });
  }
}
