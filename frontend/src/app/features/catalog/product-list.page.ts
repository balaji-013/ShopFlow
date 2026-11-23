import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable, toSignal } from '@angular/core/rxjs-interop';
import { catchError, debounceTime, distinctUntilChanged, of, switchMap, tap } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { CursorPage, Product } from '../../core/models';
import { ToastService } from '../../core/toast.service';
import { CartStore } from '../cart/cart.store';
import { ProductCard } from './product-card';

@Component({
  selector: 'app-product-list',
  imports: [ProductCard],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h1>Shop</h1>
    <div class="toolbar">
      <input #q type="search" placeholder="Search products (starts with…)" [value]="search()" (input)="search.set(q.value)" aria-label="Search products" />
      <select #c (change)="categoryId.set(c.value || null)" aria-label="Category">
        <option value="">All categories</option>
        @for (cat of categories(); track cat.id) { <option [value]="cat.id">{{ cat.name }}</option> }
      </select>
    </div>

    @if (loading()) { <p class="muted" aria-busy="true">Loading…</p> }
    @else if (items().length === 0) { <p class="muted">No products match.</p> }

    <div class="grid">
      @for (p of items(); track p.id) {          <!-- track by id: Angular moves DOM nodes instead of rebuilding them -->
        <app-product-card [product]="p" (add)="addToCart($event)" />
      }
    </div>

    @if (nextCursor()) {
      <div class="center"><button class="btn" (click)="loadMore()" [disabled]="loadingMore()">{{ loadingMore() ? 'Loading…' : 'Load more' }}</button></div>
    }`,
})
export class ProductListPage {
  private readonly api = inject(ApiService);
  private readonly cart = inject(CartStore);
  private readonly toast = inject(ToastService);

  protected readonly search = signal('');
  protected readonly categoryId = signal<string | null>(null);
  protected readonly categories = toSignal(this.api.categories(), { initialValue: [] });

  protected readonly items = signal<Product[]>([]);
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly loading = signal(true);
  protected readonly loadingMore = signal(false);

  private readonly filters = computed(() => ({ search: this.search().trim(), categoryId: this.categoryId() }));

  constructor() {
    // signal -> Observable -> RxJS pipeline -> signal.  Each operator earns its place:
    //  debounceTime       : wait for the user to stop typing (1 request, not 1 per keystroke)
    //  distinctUntilChanged: skip if the effective filter did not change
    //  switchMap          : CANCEL the in-flight request when a new one starts => a slow old response can never overwrite a newer one
    //  catchError (inside): an error ends only the inner request, NOT the whole search stream
    toObservable(this.filters).pipe(
      debounceTime(300),
      distinctUntilChanged((a, b) => a.search === b.search && a.categoryId === b.categoryId),
      tap(() => this.loading.set(true)),
      switchMap((f) => this.api.products(f).pipe(catchError(() => of<CursorPage<Product>>({ items: [], nextCursor: null })))),
      takeUntilDestroyed(),                      // unsubscribes when the component is destroyed - no leaks
    ).subscribe((page) => { this.items.set(page.items); this.nextCursor.set(page.nextCursor); this.loading.set(false); });
  }

  protected loadMore() {
    const cursor = this.nextCursor();
    if (!cursor) return;
    this.loadingMore.set(true);
    this.api.products({ ...this.filters(), cursor }).subscribe({
      next: (page) => { this.items.update((cur) => [...cur, ...page.items]); this.nextCursor.set(page.nextCursor); this.loadingMore.set(false); },
      error: () => this.loadingMore.set(false),
    });
  }

  protected addToCart(p: Product) { this.cart.add(p); this.toast.success(`${p.name} added to cart`); }
}
