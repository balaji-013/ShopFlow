import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { CurrencyPipe } from '@angular/common';
import { ApiService } from '../../core/api.service';
import { problemOf } from '../../core/http.interceptors';
import { Product } from '../../core/models';
import { ToastService } from '../../core/toast.service';

@Component({
  selector: 'app-admin-products',
  imports: [CurrencyPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h1>Manage products</h1>
    <table class="table">
      <thead><tr><th>SKU</th><th>Name</th><th>Price</th><th>Stock</th></tr></thead>
      <tbody>
        @for (p of items(); track p.id) {
          <tr>
            <td>{{ p.sku }}</td><td>{{ p.name }}</td>
            <td>
              <input #price class="num" type="number" min="0" step="0.01" [value]="p.price" />
              <button class="btn sm" (click)="savePrice(p, +price.value)">Save</button>
              <small class="muted">{{ p.price | currency }}</small>
            </td>
            <td>
              <button class="btn sm" (click)="adjust(p, -1)" [disabled]="p.stock === 0">−</button>
              <span class="qty">{{ p.stock }}</span>
              <button class="btn sm" (click)="adjust(p, 1)">+</button>
              <button class="btn sm" (click)="adjust(p, 10)">+10</button>
            </td>
          </tr>
        }
      </tbody>
    </table>
    @if (nextCursor()) { <div class="center"><button class="btn" (click)="load(nextCursor())">Load more</button></div> }`,
})
export class AdminProductsPage {
  private readonly api = inject(ApiService);
  private readonly toast = inject(ToastService);
  protected readonly items = signal<Product[]>([]);
  protected readonly nextCursor = signal<string | null>(null);

  constructor() { this.load(null); }

  protected load(cursor: string | null) {
    this.api.products({ cursor, limit: 25 }).subscribe((p) => { this.items.update((c) => [...c, ...p.items]); this.nextCursor.set(p.nextCursor); });
  }

  protected savePrice(p: Product, price: number) {
    this.api.updateProduct(p, { price }).subscribe({
      next: (fresh) => { this.replace(fresh); this.toast.success('Price saved'); },
      error: (e) => {
        if (problemOf(e).code === 'concurrency_conflict') {
          // OPTIMISTIC CONCURRENCY in the UI: someone else saved first. Do not overwrite - show them the latest and let them retry.
          this.toast.error('Someone else changed this product. Reloaded the latest version - review and save again.');
          this.api.product(p.id).subscribe((f) => this.replace(f));
        } else this.toast.error(problemOf(e).title);
      },
    });
  }

  protected adjust(p: Product, delta: number) {
    // Sends a DELTA, not "set stock to 7": two admins clicking "+1" at once both count. (Absolute writes would lose one.)
    this.api.adjustStock(p.id, delta).subscribe({ next: (f) => this.replace(f), error: (e) => this.toast.error(problemOf(e).title) });
  }

  private replace(f: Product) { this.items.update((cur) => cur.map((x) => (x.id === f.id ? f : x))); }
}
