import { DatePipe } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { toObservable } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { Store } from '@ngxs/store';
import { NzAlertModule } from 'ng-zorro-antd/alert';
import { NzButtonModule } from 'ng-zorro-antd/button';
import { NzInputModule } from 'ng-zorro-antd/input';
import { NzPopconfirmModule } from 'ng-zorro-antd/popconfirm';
import { NzSelectModule } from 'ng-zorro-antd/select';
import { NzTableModule } from 'ng-zorro-antd/table';
import { NzTagModule } from 'ng-zorro-antd/tag';
import { Observable, of } from 'rxjs';
import { SessionState } from '../core/session.state';
import { keptData } from '../shared/page-data';
import { EditName } from '../shared/edit-name';

type Role = 'Admin' | 'Trader' | 'Viewer';

interface User {
  id: number;
  email: string;
  displayName: string;
  role: Role;
  isDisabled: boolean;
  mustChangePassword: boolean;
  createdAt: string;
  /** Who put this person forward as an administrator, while a second administrator has not yet agreed. */
  adminProposedBy: string | null;
}

const ROLE_NOTE: Record<Role, string> = {
  Admin: 'Everything, including keys, limits and approving requests',
  Trader: 'Sees everything; places and cancels orders; pulls a kill switch',
  Viewer: 'Sees everything; changes nothing',
};

/** The people who can sign in, and what each may do. */
@Component({
  selector: 'app-users-page',
  imports: [DatePipe, EditName, FormsModule, NzAlertModule, NzButtonModule, NzInputModule, NzPopconfirmModule, NzSelectModule, NzTableModule, NzTagModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page-head"><h1>Users</h1><span class="sub">Requests that need a second person need at least two administrators</span></div>
    @if (error(); as message) {
      <nz-alert nzType="error" [nzMessage]="message" nzShowIcon nzCloseable (nzOnClose)="error.set(null)" style="display:block;margin-bottom:10px" data-testid="users-error" />
    }
    @if (password(); as p) {
      <nz-alert nzType="success" nzShowIcon nzCloseable (nzOnClose)="password.set(null)" style="display:block;margin-bottom:10px" data-testid="temp-password"
        [nzMessage]="'Temporary password for ' + p.email + ': ' + p.password"
        nzDescription="It is shown this once. Pass it on privately; the person must choose their own password the first time they sign in." />
    }
    <div class="panel">
      <nz-table [nzData]="users.data() ?? []" nzTemplateMode [nzShowPagination]="false" nzSize="small" data-testid="users-table">
        <thead>
          <tr><th>Name</th><th>Email</th><th>Role</th><th>Since</th><th></th></tr>
        </thead>
        <tbody>
          @for (u of users.data() ?? []; track u.id) {
            <tr>
              <td><b><app-edit-name [value]="u.displayName" [url]="'/api/users/' + u.id + '/name'" what="this person" [maxlength]="100" (saved)="again()" /></b>
                @if (u.isDisabled) {
                  <nz-tag style="margin-left:6px">Switched off</nz-tag>
                }
                @if (u.mustChangePassword) {
                  <nz-tag nzColor="gold" style="margin-left:6px">Temporary password</nz-tag>
                }
              </td>
              <td>{{ u.email }}</td>
              <td>
                <nz-select [ngModel]="u.role" (ngModelChange)="run(http.post('/api/users/' + u.id + '/role', { role: $event }))" [nzDisabled]="u.email === me()" nzSize="small" style="width:110px" [title]="roleNote[u.role]">
                  @for (r of roles; track r) {
                    <nz-option [nzValue]="r" [nzLabel]="r" />
                  }
                </nz-select>
                @if (u.adminProposedBy) {
                  <div class="hint" style="margin-top:4px;max-width:260px" [attr.data-testid]="'awaiting-' + u.id">
                    Put forward as Admin by {{ u.adminProposedBy }}.
                    @if (u.adminProposedBy.toLowerCase() !== me().toLowerCase() && u.email !== me()) {
                      <a (click)="run(http.post('/api/users/' + u.id + '/confirm-admin', {}))">Agree</a>
                    } @else {
                      Another administrator has to agree.
                    }
                  </div>
                }
              </td>
              <td class="nowrap">{{ u.createdAt | date: 'd MMM y' }}</td>
              <td class="nowrap" style="text-align:right">
                @if (u.email !== me()) {
                  @if (u.role !== 'Admin') {
                    <a nz-popconfirm nzPopconfirmTitle="Give this person a new temporary password?" nzOkText="Reset" (nzOnConfirm)="reset(u)">Reset password</a>
                  }
                  <a style="margin-left:12px" (click)="run(http.post('/api/users/' + u.id + '/disabled', { disabled: !u.isDisabled }))">{{ u.isDisabled ? 'Switch on' : 'Switch off' }}</a>
                } @else {
                  <span class="hint">You</span>
                }
              </td>
            </tr>
          }
        </tbody>
      </nz-table>
      <div class="add">
        <input nz-input [ngModel]="name()" (ngModelChange)="name.set($event)" placeholder="Name" maxlength="100" style="width:200px" data-testid="user-name" />
        <input nz-input [ngModel]="email()" (ngModelChange)="email.set($event)" placeholder="Email" maxlength="254" style="width:260px" data-testid="user-email" />
        <nz-select [ngModel]="role()" (ngModelChange)="role.set($event)" style="width:120px" data-testid="user-role">
          @for (r of roles; track r) {
            <nz-option [nzValue]="r" [nzLabel]="r" />
          }
        </nz-select>
        <button nz-button nzType="primary" [disabled]="name().trim() === '' || email().trim() === ''" (click)="add()" data-testid="user-add">Add a user</button>
        <span class="hint" style="align-self:center">{{ roleNote[role()] }}</span>
      </div>
    </div>
  `,
  styles: `
    .add {
      display: flex;
      gap: 8px;
      flex-wrap: wrap;
      margin-top: 12px;
    }
  `,
})
export class UsersPage {
  protected readonly http = inject(HttpClient);
  protected readonly roles: Role[] = ['Admin', 'Trader', 'Viewer'];
  protected readonly roleNote = ROLE_NOTE;
  private readonly session = inject(Store).selectSignal(SessionState.session);
  protected readonly me = () => this.session()?.email ?? '';

  protected readonly error = signal<string | null>(null);
  protected readonly password = signal<{ email: string; password: string } | null>(null);
  private readonly version = signal(0);
  protected readonly users = keptData(of(0), toObservable(this.version), () => this.http.get<User[]>('/api/users'));

  protected readonly name = signal('');
  protected readonly email = signal('');
  protected readonly role = signal<Role>('Trader');

  protected again(): void {
    this.version.update((v) => v + 1);
  }

  protected add(): void {
    this.run(this.http.post<{ user: User; temporaryPassword: string }>('/api/users', { email: this.email().trim(), displayName: this.name().trim(), role: this.role() }), (created) => {
      this.password.set({ email: created.user.email, password: created.temporaryPassword });
      this.name.set('');
      this.email.set('');
    });
  }

  protected reset(user: User): void {
    this.run(this.http.post<{ user: User; temporaryPassword: string }>(`/api/users/${user.id}/reset-password`, {}), (created) => this.password.set({ email: user.email, password: created.temporaryPassword }));
  }

  protected run<T>(request: Observable<T>, done?: (answer: T) => void): void {
    this.error.set(null);
    request.subscribe({
      next: (answer) => {
        done?.(answer);
        this.version.update((v) => v + 1);
      },
      error: (e: { error?: { message?: string } }) => {
        this.error.set(e.error?.message ?? 'That did not work. Try again.');
        this.version.update((v) => v + 1);
      },
    });
  }
}
