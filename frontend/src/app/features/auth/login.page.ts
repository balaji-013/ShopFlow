import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { AuthStore } from '../../core/auth.store';
import { problemOf } from '../../core/http.interceptors';

@Component({
  selector: 'app-login',
  imports: [ReactiveFormsModule, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="card narrow">
      <h1>Sign in</h1>
      <form [formGroup]="form" (ngSubmit)="submit()" novalidate>
        <label>Email <input type="email" formControlName="email" autocomplete="username" /></label>
        @if (form.controls.email.touched && form.controls.email.invalid) { <p class="field-error">Enter a valid e-mail.</p> }
        <label>Password <input type="password" formControlName="password" autocomplete="current-password" /></label>
        @if (error()) { <p class="field-error" role="alert">{{ error() }}</p> }
        <button class="btn primary" [disabled]="form.invalid || busy()">{{ busy() ? 'Signing in…' : 'Sign in' }}</button>
      </form>
      <p class="muted">No account? <a routerLink="/register">Register</a></p>
      <p class="hint">Demo: <code>demo&#64;acme.test</code> / <code>Customer#12345</code> · admin: <code>admin&#64;acme.test</code> / <code>Admin#12345</code></p>
    </section>`,
})
export class LoginPage {
  private readonly fb = inject(FormBuilder).nonNullable;      // typed forms: form.value.email is `string`, not `string | null`
  private readonly auth = inject(AuthStore);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  protected readonly busy = signal(false);
  protected readonly error = signal('');
  protected readonly form = this.fb.group({
    email: ['demo@acme.test', [Validators.required, Validators.email]],
    password: ['Customer#12345', [Validators.required]],
  });

  submit() {
    if (this.form.invalid) return;
    this.busy.set(true); this.error.set('');
    const { email, password } = this.form.getRawValue();
    this.auth.login(email, password).subscribe({
      next: () => this.router.navigateByUrl(this.route.snapshot.queryParamMap.get('returnUrl') ?? '/'),
      error: (e) => { this.busy.set(false); this.error.set(problemOf(e).status === 401 ? 'Wrong e-mail or password.' : problemOf(e).title); },
    });
  }
}
