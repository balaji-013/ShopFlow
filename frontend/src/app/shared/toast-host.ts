import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { ToastService } from '../core/toast.service';

@Component({
  selector: 'app-toast-host',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="toasts" role="status" aria-live="polite">
      @for (t of toast.toasts(); track t.id) {
        <div class="toast" [class]="'toast-' + t.kind" (click)="toast.dismiss(t.id)">{{ t.text }}</div>
      }
    </div>`,
})
export class ToastHost { protected readonly toast = inject(ToastService); }
