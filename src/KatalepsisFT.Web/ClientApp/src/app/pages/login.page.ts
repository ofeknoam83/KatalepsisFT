import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { Store } from '@ngxs/store';
import { NzAlertModule } from 'ng-zorro-antd/alert';
import { NzButtonModule } from 'ng-zorro-antd/button';
import { NzCheckboxModule } from 'ng-zorro-antd/checkbox';
import { NzFormModule } from 'ng-zorro-antd/form';
import { NzInputModule } from 'ng-zorro-antd/input';
import { apiMessage } from '../core/session';
import { Login } from '../core/session.state';

interface LoginForm {
  email: FormControl<string>;
  password: FormControl<string>;
  rememberMe: FormControl<boolean>;
}

@Component({
  selector: 'app-login-page',
  imports: [ReactiveFormsModule, NzAlertModule, NzButtonModule, NzCheckboxModule, NzFormModule, NzInputModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="auth-page">
      <form class="box" nz-form nzLayout="vertical" [formGroup]="form" (ngSubmit)="submit()">
        <div class="brand"><img class="mark" src="/favicon.svg" alt="" />Katalepsis <small>FT</small></div>
        <h1>Sign in</h1>
        <p class="hint">Multi-manager portfolio monitor</p>

        @if (error(); as message) {
          <nz-alert nzType="error" [nzMessage]="message" nzShowIcon style="display:block;margin-bottom:16px" />
        }

        <nz-form-item>
          <nz-form-label nzFor="email">Email</nz-form-label>
          <nz-form-control nzErrorTip="Enter your email">
            <input nz-input id="email" type="email" formControlName="email" autocomplete="username" />
          </nz-form-control>
        </nz-form-item>
        <nz-form-item>
          <nz-form-label nzFor="password">Password</nz-form-label>
          <nz-form-control nzErrorTip="Enter your password">
            <input nz-input id="password" type="password" formControlName="password" autocomplete="current-password" />
          </nz-form-control>
        </nz-form-item>
        <label nz-checkbox formControlName="rememberMe" style="margin-bottom:16px">Keep me signed in</label>

        <button nz-button nzType="primary" nzBlock nzSize="large" [nzLoading]="busy()">Sign in</button>
        <p class="hint" style="margin:16px 0 0;text-align:center">Accounts are created by an administrator.</p>
      </form>
    </div>
  `,
})
export class LoginPage {
  private readonly store = inject(Store);
  private readonly router = inject(Router);

  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly form = new FormGroup<LoginForm>({
    email: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    password: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    rememberMe: new FormControl(false, { nonNullable: true }),
  });

  protected submit(): void {
    if (this.form.invalid) {
      Object.values(this.form.controls).forEach((control) => {
        control.markAsDirty();
        control.updateValueAndValidity();
      });
      return;
    }

    const { email, password, rememberMe } = this.form.getRawValue();
    this.busy.set(true);
    this.error.set(null);
    this.store.dispatch(new Login(email, password, rememberMe)).subscribe({
      next: () => {
        // Back to the page that was asked for. Only a path inside this app is followed, never an address elsewhere.
        const next = this.router.parseUrl(this.router.url).queryParamMap.get('next');
        void (next !== null && next.startsWith('/') && !next.startsWith('//') && !next.startsWith('/login') ? this.router.navigateByUrl(next) : this.router.navigate(['/']));
      },
      error: (error: unknown) => {
        this.busy.set(false);
        this.error.set(apiMessage(error, 'Could not sign in. Try again.'));
      },
    });
  }
}
