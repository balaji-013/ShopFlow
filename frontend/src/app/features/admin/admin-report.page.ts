import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { CurrencyPipe } from '@angular/common';
import { toSignal } from '@angular/core/rxjs-interop';
import { catchError, of } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { RankedProduct } from '../../core/models';

@Component({
  selector: 'app-admin-report',
  imports: [CurrencyPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h1>Top products per category</h1>
    <p class="muted">Paid orders only · ranked with <code>RANK() OVER (PARTITION BY category …)</code> · cached 5 min on the server.</p>
    <table class="table">
      <thead><tr><th>Category</th><th>#</th><th>Product</th><th>Units</th><th>Revenue</th></tr></thead>
      <tbody>
        @for (r of rows(); track r.productId) {
          <tr><td>{{ r.categoryName }}</td><td>{{ r.rnk }}</td><td>{{ r.productName }}</td><td>{{ r.unitsSold }}</td><td>{{ r.revenue | currency }}</td></tr>
        } @empty { <tr><td colspan="5" class="muted">No paid orders yet - place and pay an order first.</td></tr> }
      </tbody>
    </table>`,
})
export class AdminReportPage {
  protected readonly rows = toSignal(inject(ApiService).topProducts(3).pipe(catchError(() => of<RankedProduct[]>([]))), { initialValue: [] });
}
