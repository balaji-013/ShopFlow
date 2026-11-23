import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { CurrencyPipe } from '@angular/common';
import { Product } from '../../core/models';

/** "Dumb" presentational component: data in via input(), events out via output(). No services, trivially testable, OnPush-safe. */
@Component({
  selector: 'app-product-card',
  imports: [CurrencyPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <article class="card product">
      <span class="chip">{{ product().categoryName }}</span>
      <h3>{{ product().name }}</h3>
      <p class="muted">{{ product().description }}</p>
      <div class="row between">
        <strong>{{ product().price | currency }}</strong>
        <span [class]="product().stock <= 5 ? 'low' : 'muted'">{{ product().stock === 0 ? 'Out of stock' : product().stock + ' left' }}</span>
      </div>
      <button class="btn primary" [disabled]="product().stock === 0" (click)="add.emit(product())">Add to cart</button>
    </article>`,
})
export class ProductCard {
  readonly product = input.required<Product>();
  readonly add = output<Product>();
}
