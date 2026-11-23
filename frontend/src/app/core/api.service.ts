import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { Category, CursorPage, Order, Product, RankedProduct } from './models';

export interface ProductFilter { search?: string; categoryId?: string | null; cursor?: string | null; limit?: number; }

/** The ONLY place that knows URLs. Components depend on typed methods, never on strings (easy to mock, easy to refactor). */
@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/v1';     // relative => same origin via the dev proxy / reverse proxy in prod => no CORS, SameSite cookies work

  products(f: ProductFilter): Observable<CursorPage<Product>> {
    let params = new HttpParams().set('limit', f.limit ?? 12);
    if (f.search) params = params.set('search', f.search);
    if (f.categoryId) params = params.set('categoryId', f.categoryId);
    if (f.cursor) params = params.set('cursor', f.cursor);
    return this.http.get<CursorPage<Product>>(`${this.base}/products`, { params });
  }
  product(id: string) { return this.http.get<Product>(`/products/${id}`); }
  categories() { return this.http.get<Category[]>(`${this.base}/categories`); }

  updateProduct(p: Product, patch: { price: number }) {
    return this.http.put<Product>(`${this.base}/products/${p.id}`,
      { name: p.name, description: p.description, price: patch.price, categoryId: p.categoryId, version: p.version });
  }
  adjustStock(id: string, delta: number) { return this.http.post<Product>(`${this.base}/products/${id}/stock-adjustments`, { delta }); }

  placeOrder(items: { productId: string; quantity: number }[], idempotencyKey: string) {
    return this.http.post<Order>(`${this.base}/orders`, { items }, { headers: { 'Idempotency-Key': idempotencyKey } });
  }
  orders(cursor?: string | null, scope: 'mine' | 'all' = 'mine') {
    let params = new HttpParams().set('limit', 10).set('scope', scope);
    if (cursor) params = params.set('cursor', cursor);
    return this.http.get<CursorPage<Order>>(`${this.base}/orders`, { params });
  }
  order(id: string) { return this.http.get<Order>(`${this.base}/orders/${id}`); }
  cancelOrder(id: string) { return this.http.post<Order>(`${this.base}/orders/${id}/cancel`, {}); }
  startPayment(id: string) { return this.http.post<{ reference: string }>(`${this.base}/orders/${id}/payment-intents`, {}); }
  simulatePayment(id: string) { return this.http.post<void>(`${this.base}/dev/orders/${id}/simulate-payment`, {}); }
  ship(id: string) { return this.http.post<Order>(`${this.base}/orders/${id}/ship`, {}); }
  deliver(id: string) { return this.http.post<Order>(`${this.base}/orders/${id}/deliver`, {}); }
  topProducts(top = 3) { return this.http.get<RankedProduct[]>(`${this.base}/reports/top-products`, { params: { top } }); }
}

