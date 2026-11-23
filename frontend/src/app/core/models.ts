// Mirror of the API contract (backend DTOs). In a bigger team this file is GENERATED from the OpenAPI document
// (openapi-typescript / NSwag) so the two sides can never drift apart.

export type Role = 'Customer' | 'Admin';
export type OrderStatus = 'Pending' | 'Paid' | 'Shipped' | 'Delivered' | 'Cancelled';

export interface User { id: string; email: string; fullName: string; role: Role; }
export interface Session { accessToken: string; expiresIn: number; user: User; }

export interface Category { id: string; name: string; }
export interface Product {
  id: string; sku: string; name: string; description: string; price: number; stock: number;
  categoryId: string; categoryName: string; version: string; createdAt: string;
}
export interface CursorPage<T> { items: T[]; nextCursor: string | null; }

export interface OrderItem { productId: string; productName: string; unitPrice: number; quantity: number; }
export interface Order {
  id: string; status: OrderStatus; total: number; createdAt: string; paidAt: string | null;
  paymentReference: string | null; items: OrderItem[]; version: string;
}
export interface RankedProduct { categoryName: string; productId: string; productName: string; revenue: number; unitsSold: number; rnk: number; }

/** RFC 9457 problem+json as emitted by the API's GlobalExceptionHandler. */
export interface ApiProblem { status: number; title: string; code: string; errors?: Record<string, string[]>; }
export interface OrderUpdate { orderId: string; status: OrderStatus; }
