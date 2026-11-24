import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { Store } from '@ngxs/store';
import { NzAlertModule } from 'ng-zorro-antd/alert';
import { NzButtonModule } from 'ng-zorro-antd/button';
import { NzFormModule } from 'ng-zorro-antd/form';
import { NzInputModule } from 'ng-zorro-antd/input';
import { apiMessage } from '../core/session';
import { ChangePassword, Logout, SessionState } from '../core/session.state';

interface ChangePasswordForm {
  current: FormControl<string>;
  next: FormControl<string>;
  repeat: FormControl<string>;
}

@Component({
  selector: 'app-change-password-page',
  imports: [ReactiveFormsModule, NzAlertModule, NzButtonModule, NzFormModule, NzInputModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="auth-page">
      <form class="box" nz-form nzLayout="vertical" [formGroup]="form" (ngSubmit)="submit()">
        <div class="brand"><img class="mark" src="/favicon.svg" alt="" /><span class="name">Katalepsis<small>Fund Terminal</small></span></div>
        <h1>Change your password</h1>
        @if (session()?.mustChangePassword) {
          <p class="hint">Your account still has the password it was created with. Choose your own to continue.</p>
        } @else {
          <p class="hint">Signed in as {{ session()?.email }}</p>
        }

        @if (error(); as message) {
          <nz-alert nzType="error" [nzMessage]="message" nzShowIcon style="display:block;margin-bottom:16px" />
        }

        <nz-form-item>
          <nz-form-label nzFor="current">Current password</nz-form-label>
          <nz-form-control nzErrorTip="Enter your current password">
            <input nz-input id="current" type="password" formControlName="current" autocomplete="current-password" />
          </nz-form-control>
        </nz-form-item>
        <nz-form-item>
          <nz-form-label nzFor="next">New password</nz-form-label>
          <nz-form-control nzErrorTip="At least 12 characters" nzExtra="At least 12 characters, with upper and lower case, a digit and a symbol.">
            <input nz-input id="next" type="password" formControlName="next" autocomplete="new-password" />
          </nz-form-control>
        </nz-form-item>
        <nz-form-item>
          <nz-form-label nzFor="repeat">Repeat the new password</nz-form-label>
          <nz-form-control nzErrorTip="Repeat the new password">
            <input nz-input id="repeat" type="password" formControlName="repeat" autocomplete="new-password" />
          </nz-form-control>
        </nz-form-item>

        <button nz-button nzType="primary" nzBlock nzSize="large" [nzLoading]="busy()">Change password</button>
        <button nz-button nzType="link" nzBlock type="button" style="margin-top:8px" (click)="leave()">
          {{ session()?.mustChangePassword ? 'Sign out' : 'Cancel' }}
        </button>
      </form>
    </div>
  `,
})
export class ChangePasswordPage {
  private readonly store = inject(Store);
  private readonly router = inject(Router);

  protected readonly session = this.store.selectSignal(SessionState.session);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly form = new FormGroup<ChangePasswordForm>({
    current: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    next: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.minLength(12)] }),
    repeat: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
  });

  protected submit(): void {
    if (this.form.invalid) {
      Object.values(this.form.controls).forEach((control) => {
        control.markAsDirty();
        control.updateValueAndValidity();
      });
      return;
    }

    const { current, next, repeat } = this.form.getRawValue();
    if (next !== repeat) {
      this.error.set('The two new passwords are not the same.');
      return;
    }

    this.busy.set(true);
    this.error.set(null);
    this.store.dispatch(new ChangePassword(current, next)).subscribe({
      next: () => void this.router.navigate(['/']),
      error: (error: unknown) => {
        this.busy.set(false);
        this.error.set(apiMessage(error, 'Could not change the password. Try again.'));
      },
    });
  }

  protected leave(): void {
    if (this.session()?.mustChangePassword) {
      this.store.dispatch(new Logout()).subscribe(() => void this.router.navigate(['/login']));
    } else {
      void this.router.navigate(['/']);
    }
  }
}
