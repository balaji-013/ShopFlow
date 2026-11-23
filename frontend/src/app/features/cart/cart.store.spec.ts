import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { CartStore } from './cart.store';

describe('CartStore', () => {
  let cart: CartStore;
  beforeEach(() => { localStorage.clear(); TestBed.configureTestingModule({}); cart = TestBed.inject(CartStore); });

  const widget = { id: 'p1', name: 'Widget', price: 10 };

  it('merges the same product into one line and computes derived totals', () => {
    cart.add(widget); cart.add(widget, 2); cart.add({ id: 'p2', name: 'Gadget', price: 5.5 });
    expect(cart.lines()).toHaveLength(2);
    expect(cart.count()).toBe(4);
    expect(cart.total()).toBeCloseTo(35.5);
  });

  it('removes a line when quantity reaches zero', () => {
    cart.add(widget); cart.setQuantity('p1', 0);
    expect(cart.lines()).toHaveLength(0);
  });

  it('changes the idempotency key whenever the cart content changes (same key + new body would be rejected by the API)', () => {
    const k1 = cart.idempotencyKey();
    cart.add(widget);
    expect(cart.idempotencyKey()).not.toBe(k1);
  });

  it('serialises to the API request shape (no prices - the server decides prices)', () => {
    cart.add(widget, 3);
    expect(cart.toRequestItems()).toEqual([{ productId: 'p1', quantity: 3 }]);
  });
});
