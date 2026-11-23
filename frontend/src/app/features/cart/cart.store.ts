import { Injectable, computed, effect, signal } from '@angular/core';
import { Product } from '../../core/models';

export interface CartLine { productId: string; name: string; unitPrice: number; quantity: number; }
const STORAGE_KEY = 'shopflow.cart.v1';

/**
 * Signal-based store: `signal` = state, `computed` = derived state (memoised, never stale), `effect` = side effect (persist).
 * No NgRx needed: a cart is small, local, and synchronous. (Server state - products, orders - is fetched, not stored globally.)
 */
@Injectable({ providedIn: 'root' })
export class CartStore {
  private readonly _lines = signal<CartLine[]>(this.load());
  /** Idempotency key for the checkout. It MUST change whenever the cart content changes (same key + different body = 422). */
  private _key = signal(crypto.randomUUID());

  readonly lines = this._lines.asReadonly();
  readonly count = computed(() => this._lines().reduce((n, l) => n + l.quantity, 0));
  readonly total = computed(() => this._lines().reduce((sum, l) => sum + l.unitPrice * l.quantity, 0));
  readonly idempotencyKey = this._key.asReadonly();

  constructor() {
    effect(() => {
      const lines = this._lines();
      try { localStorage.setItem(STORAGE_KEY, JSON.stringify(lines)); } catch { /* private mode / quota: cart just isn't persisted */ }
    });
  }

  add(p: Pick<Product, 'id' | 'name' | 'price'>, qty = 1) {
    this.mutate((lines) => {
      const i = lines.findIndex((l) => l.productId === p.id);
      if (i === -1) return [...lines, { productId: p.id, name: p.name, unitPrice: p.price, quantity: qty }];
      return lines.map((l, idx) => (idx === i ? { ...l, quantity: l.quantity + qty } : l));
    });
  }
  setQuantity(productId: string, quantity: number) {
    this.mutate((lines) => (quantity <= 0 ? lines.filter((l) => l.productId !== productId)
      : lines.map((l) => (l.productId === productId ? { ...l, quantity: Math.min(quantity, 1000) } : l))));
  }
  remove(productId: string) { this.setQuantity(productId, 0); }
  clear() { this.mutate(() => []); }

  toRequestItems() { return this._lines().map((l) => ({ productId: l.productId, quantity: l.quantity })); }

  private mutate(fn: (l: CartLine[]) => CartLine[]) {
    this._lines.update(fn);          // immutable updates: signals compare by reference
    this._key.set(crypto.randomUUID());
  }

  private load(): CartLine[] {
    try { return JSON.parse(localStorage.getItem(STORAGE_KEY) ?? '[]') as CartLine[]; } catch { return []; }
  }
}
