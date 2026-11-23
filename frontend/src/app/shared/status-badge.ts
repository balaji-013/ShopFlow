import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { OrderStatus } from '../core/models';

@Component({
  selector: 'app-status-badge',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span class="badge" [class]="cls()">{{ status() }}</span>`,
})
export class StatusBadge {
  readonly status = input.required<OrderStatus>();          // signal input: typed, required, reactive
  protected readonly cls = computed(() => 'badge-' + this.status().toLowerCase());
}
