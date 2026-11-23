import { ChangeDetectionStrategy, Component, effect, inject, signal } from '@angular/core';
import { CurrencyPipe, DatePipe } from '@angular/common';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Observable, concatMap, switchMap } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { AuthStore } from '../../core/auth.store';
import { problemOf } from '../../core/http.interceptors';
import { Order } from '../../core/models';
import { RealtimeService } from '../../core/realtime.service';
import { ToastService } from '../../core/toast.service';
import { StatusBadge } from '../../shared/status-badge';

@Component({
  selector: 'app-orders',
  imports: [CurrencyPipe, DatePipe, StatusBadge],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="row between">
      <h1>Orders</h1>
      @if (auth.isAdmin()) {
        <label class="inline"><input type="checkbox" [checked]="scope() === 'all'" (change)="scope.set(scope() === 'all' ? 'mine' : 'all')" /> All customers (admin)</label>
      }
    </div>
    @if (orders().length === 0 && !loading()) { <p class="muted">No orders yet.</p> }

    @for (o of orders(); track o.id) {
      <article class="card">
        <div class="row between">
          <div><strong>#{{ o.id.slice(0, 8) }}</strong> <span class="muted">{{ o.createdAt | date: 'medium' }}</span></div>
          <app-status-badge [status]="o.status" />
        </div>
        <ul class="plain">
          @for (i of o.items; track i.productId) { <li>{{ i.quantity }} × {{ i.productName }} <span class="muted">@ {{ i.unitPrice | currency }}</span></li> }
        </ul>
        <div class="row between">
          <strong>{{ o.total | currency }}</strong>
          <div class="actions">
            @if (o.status === 'Pending') {
              <button class="btn primary sm" (click)="pay(o)" [disabled]="busyId() === o.id">Pay (simulated)</button>
              <button class="btn danger sm" (click)="run(o, api.cancelOrder(o.id))" [disabled]="busyId() === o.id">Cancel</button>
            }
            @if (auth.isAdmin() && o.status === 'Paid') { <button class="btn sm" (click)="run(o, api.ship(o.id))">Mark shipped</button> }
            @if (auth.isAdmin() && o.status === 'Shipped') { <button class="btn sm" (click)="run(o, api.deliver(o.id))">Mark delivered</button> }
          </div>
        </div>
      </article>
    }
    @if (nextCursor()) { <div class="center"><button class="btn" (click)="loadMore()">Load more</button></div> }`,
})
export class OrdersPage {
  protected readonly api = inject(ApiService);
  protected readonly auth = inject(AuthStore);
  private readonly toast = inject(ToastService);
  private readonly realtime = inject(RealtimeService);

  protected readonly orders = signal<Order[]>([]);
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly loading = signal(true);
  protected readonly busyId = signal<string | null>(null);
  protected readonly scope = signal<'mine' | 'all'>('mine');

  constructor() {
    effect(() => { const s = this.scope(); this.reload(s); });         // re-runs when the toggle changes

    // Server push: when the outbox worker publishes "order.updated", re-fetch that ONE order and merge it into the list.
    // concatMap keeps updates in order. (No polling: the list updates itself the moment the webhook lands.)
    this.realtime.orderUpdates$.pipe(
      concatMap((u) => this.api.order(u.orderId)),
      takeUntilDestroyed(),
    ).subscribe({ next: (o) => this.upsert(o), error: () => { /* order may belong to another scope; ignore */ } });
  }

  private reload(scope: 'mine' | 'all') {
    this.loading.set(true);
    this.api.orders(null, scope).subscribe({
      next: (p) => { this.orders.set(p.items); this.nextCursor.set(p.nextCursor); this.loading.set(false); },
      error: () => this.loading.set(false),
    });
  }

  protected loadMore() {
    this.api.orders(this.nextCursor(), this.scope()).subscribe((p) => { this.orders.update((cur) => [...cur, ...p.items]); this.nextCursor.set(p.nextCursor); });
  }

  protected pay(o: Order) {
    this.busyId.set(o.id);
    // startPayment = real flow (talks to the payment provider through the resilient client). simulatePayment plays the provider's webhook.
    this.api.startPayment(o.id).pipe(
      switchMap(() => this.api.simulatePayment(o.id)),
      switchMap(() => this.api.order(o.id)),
    ).subscribe({
      next: (fresh) => { this.upsert(fresh); this.busyId.set(null); this.toast.success('Payment received'); },
      error: (e) => { this.busyId.set(null); this.toast.error(problemOf(e).title); },
    });
  }

  protected run(o: Order, action$: Observable<Order>) {
    this.busyId.set(o.id);
    action$.subscribe({
      next: (fresh) => { this.upsert({ ...o, ...fresh }); this.busyId.set(null); },
      error: (e) => { this.busyId.set(null); this.toast.error(problemOf(e).title); this.api.order(o.id).subscribe((f) => this.upsert(f)); },
    });
  }

  private upsert(o: Order) {
    this.orders.update((cur) => (cur.some((x) => x.id === o.id) ? cur.map((x) => (x.id === o.id ? o : x)) : [o, ...cur]));
  }
}
