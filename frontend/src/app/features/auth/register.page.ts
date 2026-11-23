import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AuthStore } from '../../core/auth.store';
import { problemOf } from '../../core/http.interceptors';

@Component({
  selector: 'app-register',
  imports: [ReactiveFormsModule, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="card narrow">
      <h1>Create account</h1>
      <form [formGroup]="form" (ngSubmit)="submit()" novalidate>
        <label>Store <select formControlName="tenantCode"><option value="acme">Acme Electronics</option><option value="globex">Globex Gadgets</option></select></label>
        <label>Full name <input formControlName="fullName" /></label>
        <label>Email <input type="email" formControlName="email" /></label>
        <label>Password <input type="password" formControlName="password" autocomplete="new-password" /></label>
        @if (form.controls.password.touched && form.controls.password.invalid) { <p class="field-error">At least 10 characters.</p> }
        @for (e of serverErrors(); track e) { <p class="field-error" role="alert">{{ e }}</p> }
        <button class="btn primary" [disabled]="form.invalid || busy()">Register</button>
      </form>
      <p class="muted">Already registered? <a routerLink="/login">Sign in</a></p>
    </section>`,
})
export class RegisterPage {
  private readonly fb = inject(FormBuilder).nonNullable;
  private readonly auth = inject(AuthStore);
  private readonly router = inject(Router);

  protected readonly busy = signal(false);
  protected readonly serverErrors = signal<string[]>([]);
  protected readonly form = this.fb.group({
    tenantCode: ['acme', Validators.required],
    fullName: ['', [Validators.required, Validators.maxLength(120)]],
    email: ['', [Validators.required, Validators.email]],
    password: ['', [Validators.required, Validators.minLength(10)]],
  });

  submit() {
    if (this.form.invalid) return;
    this.busy.set(true); this.serverErrors.set([]);
    this.auth.register(this.form.getRawValue()).subscribe({
      next: () => this.router.navigateByUrl('/'),
      error: (e) => {
        this.busy.set(false);
        const p = problemOf(e);
        // Server-side validation is the real validation; client rules above are only UX. Show whatever the API says.
        this.serverErrors.set(p.errors ? Object.values(p.errors).flat() : [p.title]);
      },
    });
  }
}
