import { Injectable, effect, inject } from '@angular/core';
import { HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { AuthStore } from './auth.store';
import { OrderUpdate } from './models';

/** Wraps SignalR so components just subscribe to an Observable and never touch the transport. */
@Injectable({ providedIn: 'root' })
export class RealtimeService {
  private readonly auth = inject(AuthStore);
  private hub: HubConnection | null = null;
  private readonly updates = new Subject<OrderUpdate>();
  readonly orderUpdates$ = this.updates.asObservable();

  constructor() {
    // Reacts to login/logout only (isAuthenticated), not to every token refresh.
    effect(() => {
      if (this.auth.isAuthenticated()) void this.connect();
      else void this.disconnect();
    });
  }

  private async connect() {
    if (this.hub) return;
    this.hub = new HubConnectionBuilder()
      .withUrl('/hubs/orders', { accessTokenFactory: () => this.auth.accessToken() ?? '' })   // always the CURRENT token on (re)connect
      .withAutomaticReconnect()                                                              // 0s, 2s, 10s, 30s backoff
      .configureLogging(LogLevel.Warning)
      .build();
    this.hub.on('order.updated', (u: OrderUpdate) => this.updates.next(u));
    try { await this.hub.start(); } catch { /* the next login/refresh will retry; the UI works without live updates */ }
  }

  private async disconnect() {
    const h = this.hub; this.hub = null;
    if (h) await h.stop();
  }
}
