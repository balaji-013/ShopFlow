import { Routes } from '@angular/router';
import { adminGuard, authGuard, guestGuard } from './core/guards';

/** Every page is a lazy chunk (loadComponent): the browser downloads a screen's code only when it is visited => small initial bundle. */
export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'shop' },
  { path: 'login', title: 'Sign in', canActivate: [guestGuard], loadComponent: () => import('./features/auth/login.page').then((m) => m.LoginPage) },
  { path: 'register', title: 'Register', canActivate: [guestGuard], loadComponent: () => import('./features/auth/register.page').then((m) => m.RegisterPage) },
  { path: 'shop', title: 'Shop', canActivate: [authGuard], loadComponent: () => import('./features/catalog/product-list.page').then((m) => m.ProductListPage) },
  { path: 'cart', title: 'Cart', canActivate: [authGuard], loadComponent: () => import('./features/cart/cart.page').then((m) => m.CartPage) },
  { path: 'orders', title: 'Orders', canActivate: [authGuard], loadComponent: () => import('./features/orders/orders.page').then((m) => m.OrdersPage) },
  {
    path: 'admin', canActivate: [authGuard, adminGuard],
    children: [
      { path: 'products', title: 'Manage products', loadComponent: () => import('./features/admin/admin-products.page').then((m) => m.AdminProductsPage) },
      { path: 'report', title: 'Report', loadComponent: () => import('./features/admin/admin-report.page').then((m) => m.AdminReportPage) },
    ],
  },
  { path: '**', redirectTo: 'shop' },
];
