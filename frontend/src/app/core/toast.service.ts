import { Injectable, signal } from '@angular/core';

export interface Toast { id: number; kind: 'info' | 'success' | 'error'; text: string; }

@Injectable({ providedIn: 'root' })
export class ToastService {
  private seq = 0;
  readonly toasts = signal<Toast[]>([]);

  show(text: string, kind: Toast['kind'] = 'info', ms = 4500) {
    const id = ++this.seq;
    this.toasts.update((t) => [...t, { id, kind, text }]);     // immutable update => signal change detected
    setTimeout(() => this.dismiss(id), ms);
  }
  success(text: string) { this.show(text, 'success'); }
  error(text: string) { this.show(text, 'error', 7000); }
  dismiss(id: number) { this.toasts.update((t) => t.filter((x) => x.id !== id)); }
}
