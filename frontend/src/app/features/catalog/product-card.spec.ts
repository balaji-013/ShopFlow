import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { Product } from '../../core/models';
import { ProductCard } from './product-card';

const product = (stock: number): Product => ({
  id: 'p1', sku: 'S', name: 'Laptop Pro 1', description: 'd', price: 999, stock, categoryId: 'c', categoryName: 'Laptops', version: 'v', createdAt: '',
});

describe('ProductCard', () => {
  it('emits the product when "Add to cart" is clicked', () => {
    const fixture = TestBed.createComponent(ProductCard);
    fixture.componentRef.setInput('product', product(10));
    fixture.detectChanges();
    let emitted: Product | undefined;
    fixture.componentInstance.add.subscribe((p) => (emitted = p));
    (fixture.nativeElement.querySelector('button') as HTMLButtonElement).click();
    expect(emitted?.id).toBe('p1');
  });

  it('disables the button when out of stock', () => {
    const fixture = TestBed.createComponent(ProductCard);
    fixture.componentRef.setInput('product', product(0));
    fixture.detectChanges();
    const btn = fixture.nativeElement.querySelector('button') as HTMLButtonElement;
    expect(btn.disabled).toBe(true);
    expect(fixture.nativeElement.textContent).toContain('Out of stock');
  });
});
