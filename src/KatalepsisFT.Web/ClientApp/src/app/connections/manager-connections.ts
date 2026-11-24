import { DatePipe } from '@angular/common';
import { EditName } from '../shared/edit-name';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { NzAlertModule } from 'ng-zorro-antd/alert';
import { NzButtonModule } from 'ng-zorro-antd/button';
import { NzFormModule } from 'ng-zorro-antd/form';
import { NzInputModule } from 'ng-zorro-antd/input';
import { NzModalModule } from 'ng-zorro-antd/modal';
import { NzPopconfirmModule } from 'ng-zorro-antd/popconfirm';
import { NzSelectModule } from 'ng-zorro-antd/select';
import { NzTableModule } from 'ng-zorro-antd/table';
import { NzTagModule } from 'ng-zorro-antd/tag';
import { Observable, combineLatest } from 'rxjs';
import { LiveService } from '../core/live.service';
import { FORMAT_PIPES } from '../shared/format';
import { keptData } from '../shared/page-data';
import { ConnectionsApi, Venue } from './connections.api';

const STATE_COLOR = { Live: 'green', Failed: 'red', Pending: 'blue', Disabled: 'default' } as const;

interface KeyForm {
  venue: FormControl<Venue | null>;
  label: FormControl<string>;
  apiKey: FormControl<string>;
  apiSecret: FormControl<string>;
  passphrase: FormControl<string>;
}

/**
 * The exchange keys and wallets one manager is read through, with a way to add more. A manager's book is the sum of
 * all of them: three wallets and an exchange account are one manager with four connections. Admin only.
 */
@Component({
  selector: 'kft-manager-connections',
  imports: [DatePipe, EditName, ReactiveFormsModule, NzAlertModule, NzButtonModule, NzFormModule, NzInputModule, NzModalModule, NzPopconfirmModule, NzSelectModule, NzTableModule, NzTagModule, ...FORMAT_PIPES],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (loaded.failed()) {
      <nz-alert nzType="error" nzMessage="Could not load this manager's keys and wallets." nzShowIcon style="display:block;margin-bottom:14px" />
    }
    @if (error(); as message) {
      <nz-alert nzType="error" [nzMessage]="message" nzShowIcon nzCloseable (nzOnClose)="error.set(null)" style="display:block;margin-bottom:14px" />
    }
    @if (loaded.data(); as c) {
      <div class="panel">
        <h3>
          Keys and wallets
          <span class="hint">The manager's book is everything these hold, added up. Any number of wallets and exchange accounts can be connected.</span>
          <button nz-button nzType="primary" nzSize="small" style="margin-left:12px" [disabled]="!c.canAddKeys" (click)="open()" data-testid="add-connection">Add a key or wallet</button>
        </h3>
        @if (!c.canAddKeys) {
          <p style="color:var(--kft-text-3);font-size:12px">Keys cannot be stored on this server yet: its master key is not configured.</p>
        }
        <nz-table [nzData]="rows()" nzTemplateMode [nzShowPagination]="false" nzSize="small" data-testid="manager-connections">
          <thead>
            <tr><th>Venue</th><th>Label</th><th>Key or address</th><th>State</th><th>Last read</th><th class="num">Positions</th><th class="num">Value</th><th></th></tr>
          </thead>
          <tbody>
            @for (row of rows(); track row.id) {
              <tr>
                <td>{{ row.venueName }}</td>
                <td><app-edit-name [value]="row.label" [url]="'/api/connections/' + row.id + '/label'" what="this key or wallet" [maxlength]="100" /></td>
                <td class="nowrap">…{{ row.keyHint }} @if (!row.permissionsVerified) {<nz-tag nzColor="orange">Permissions unverified</nz-tag>}</td>
                <td>
                  <nz-tag [nzColor]="stateColor[row.state]">{{ row.state }}</nz-tag>
                  @if (row.lastError) {
                    <div style="color:#cf1322;font-size:12px;max-width:320px">{{ row.lastError }}</div>
                  }
                  @for (note of row.notes; track note) {
                    <div style="color:#ad6800;font-size:12px;max-width:320px">{{ note }}</div>
                  }
                </td>
                <td class="nowrap">{{ row.lastSuccessAt ? (row.lastSuccessAt | date: 'MM-dd-yyyy HH:mm:ss' : 'UTC') : 'Never' }}</td>
                <td class="num">{{ row.positions }}</td>
                <td class="num">{{ row.computedNavUsd | usd }}</td>
                <td class="nowrap">
                  @if (row.state !== 'Disabled') {
                    <a (click)="act(api.sync(row.id))">Read now</a>
                    <a style="margin-left:12px" (click)="act(api.setEnabled(row.id, false))">Switch off</a>
                  } @else {
                    <a (click)="act(api.setEnabled(row.id, true))">Switch on</a>
                  }
                  <a style="margin-left:12px;color:#cf1322" nz-popconfirm nzPopconfirmTitle="Remove it? Its positions leave the manager's book; history stays." nzOkText="Remove" (nzOnConfirm)="act(api.remove(row.id))">Remove</a>
                </td>
              </tr>
            }
            @if (rows().length === 0) {
              <tr><td colspan="8" style="text-align:center;color:var(--kft-text-3);padding:28px">No keys or wallets yet. Add the first one, or enter positions by hand on the next tab.</td></tr>
            }
            @if (rows().length > 1) {
              <tr><td colspan="6"><b>All together</b></td><td class="num"><b>{{ total() | usd }}</b></td><td></td></tr>
            }
          </tbody>
        </nz-table>
      </div>

      <nz-modal [nzVisible]="adding()" nzTitle="Add a read-only key or a wallet address" [nzOkText]="another() ? 'Check, save and add another' : 'Check and save'" [nzOkLoading]="saving()" [nzOkDisabled]="status() !== 'VALID' || incomplete()" (nzOnOk)="submit()" (nzOnCancel)="adding.set(false)">
        <ng-container *nzModalContent>
          <form nz-form nzLayout="vertical" [formGroup]="form" autocomplete="off">
            @if (formError(); as message) {
              <nz-alert nzType="error" [nzMessage]="message" nzShowIcon style="display:block;margin-bottom:14px" data-testid="connection-error" />
            }
            @if (saved(); as label) {
              <nz-alert nzType="success" [nzMessage]="label + ' was saved and read. Add the next one, or close.'" nzShowIcon style="display:block;margin-bottom:14px" data-testid="connection-saved" />
            }
            <div style="display:grid;grid-template-columns:1fr 1fr;gap:12px">
              <nz-form-item>
                <nz-form-label>Venue</nz-form-label>
                <nz-select formControlName="venue" nzShowSearch style="width:100%" data-testid="connection-venue">
                  @for (v of c.venues; track v.venue) {
                    <nz-option [nzValue]="v.venue" [nzLabel]="v.name" />
                  }
                </nz-select>
              </nz-form-item>
              <nz-form-item><nz-form-label>Label</nz-form-label><input nz-input formControlName="label" placeholder="Main account" maxlength="100" data-testid="connection-label" /></nz-form-item>
            </div>
            @if (usesAddress()) {
              <nz-form-item><nz-form-label>Wallet address</nz-form-label><input nz-input formControlName="apiKey" autocomplete="off" placeholder="Paste the address" data-testid="connection-key" /></nz-form-item>
              <p style="color:var(--kft-text-3);font-size:12px;margin:0 0 10px">These accounts are public on their chain. Only the address is needed, and nothing can be traded or withdrawn through it.</p>
            } @else {
              <nz-form-item><nz-form-label>API key (read-only)</nz-form-label><input nz-input formControlName="apiKey" autocomplete="off" data-testid="connection-key" /></nz-form-item>
              <nz-form-item><nz-form-label>API secret</nz-form-label><input nz-input type="password" formControlName="apiSecret" autocomplete="new-password" /></nz-form-item>
              @if (needsPassphrase()) {
                <nz-form-item><nz-form-label>Passphrase</nz-form-label><input nz-input type="password" formControlName="passphrase" autocomplete="new-password" /></nz-form-item>
              }
              <p style="color:var(--kft-text-3);font-size:12px;margin:0 0 10px">The key is checked with the venue before it is saved, stored encrypted, and never shown again.</p>
            }
            <label style="font-size:13px"><input type="checkbox" [checked]="another()" (change)="another.set($any($event.target).checked)" data-testid="connection-another" /> Keep this open to add another after saving</label>
          </form>
        </ng-container>
      </nz-modal>
    }
  `,
})
export class ManagerConnections {
  readonly fundId = input.required<number>();
  readonly managerId = input.required<number>();

  protected readonly api = inject(ConnectionsApi);
  protected readonly stateColor = STATE_COLOR;
  protected readonly error = signal<string | null>(null);
  protected readonly formError = signal<string | null>(null);
  protected readonly saved = signal<string | null>(null);
  protected readonly adding = signal(false);
  protected readonly saving = signal(false);
  protected readonly another = signal(false);
  private readonly version = signal(0);

  protected readonly loaded = keptData(
    toObservable(this.fundId),
    combineLatest([toObservable(this.version), toObservable(inject(LiveService).slowTick)]),
    (fundId) => this.api.list(fundId),
    (fundId) => fundId,
  );
  protected readonly rows = computed(() => (this.loaded.data()?.connections ?? []).filter((c) => c.managerId === this.managerId()));
  protected readonly total = computed(() => this.rows().reduce((sum, c) => sum + (c.state === 'Disabled' ? 0 : (c.computedNavUsd ?? 0)), 0));

  protected readonly form = new FormGroup<KeyForm>({
    venue: new FormControl<Venue | null>(null, { validators: [Validators.required] }),
    label: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    apiKey: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    apiSecret: new FormControl('', { nonNullable: true }),
    passphrase: new FormControl('', { nonNullable: true }),
  });
  /**
   * Whether the form is valid, as a signal. The inputs live in the dialog's overlay, and typing there does not by
   * itself redraw this component; reading the form's own "invalid" in the template would leave the button stale.
   */
  protected readonly status = toSignal(this.form.statusChanges, { initialValue: this.form.status });
  private readonly venue = toSignal(this.form.controls.venue.valueChanges, { initialValue: null });
  private readonly secret = toSignal(this.form.controls.apiSecret.valueChanges, { initialValue: '' });
  protected readonly usesAddress = computed(() => this.loaded.data()?.venues.find((v) => v.venue === this.venue())?.usesAddress ?? false);
  protected readonly needsPassphrase = computed(() => this.loaded.data()?.venues.find((v) => v.venue === this.venue())?.needsPassphrase ?? false);
  /** A key needs its secret; a wallet address has none. */
  protected readonly incomplete = computed(() => !this.usesAddress() && this.secret().trim() === '');

  protected open(): void {
    this.formError.set(null);
    this.saved.set(null);
    // Another wallet of the kind this manager already has is the likeliest thing to add next.
    const usual = this.rows().at(-1)?.venue ?? this.loaded.data()?.venues[0]?.venue ?? null;
    this.form.reset({ venue: usual, label: this.nextLabel(), apiKey: '', apiSecret: '', passphrase: '' });
    this.adding.set(true);
  }

  /** "Main account" for the first; "Account 2", "Account 3"… after that, so several wallets are told apart without typing. */
  private nextLabel(): string {
    const count = this.rows().length;
    return count === 0 ? 'Main account' : `Account ${count + 1}`;
  }

  protected submit(): void {
    const value = this.form.getRawValue();
    const wallet = this.usesAddress();
    this.saving.set(true);
    this.formError.set(null);
    this.saved.set(null);
    this.api.add({ managerId: this.managerId(), venue: value.venue!, label: value.label.trim(), apiKey: value.apiKey, apiSecret: wallet ? null : value.apiSecret, passphrase: wallet ? null : value.passphrase || null }).subscribe({
      next: (connection) => {
        this.saving.set(false);
        this.version.update((v) => v + 1);
        if (!this.another()) {
          this.adding.set(false);
          return;
        }

        // Same venue, next label, empty key: ready for the next wallet.
        this.saved.set(connection.label);
        this.form.reset({ venue: value.venue, label: `Account ${this.rows().length + 2}`, apiKey: '', apiSecret: '', passphrase: '' });
      },
      error: (e: { error?: { message?: string } }) => {
        this.saving.set(false);
        this.formError.set(e.error?.message ?? 'It could not be saved.');
      },
    });
  }

  protected act(request: Observable<unknown>): void {
    this.error.set(null);
    request.subscribe({
      next: () => this.version.update((v) => v + 1),
      error: (e: { error?: { message?: string } }) => this.error.set(e.error?.message ?? 'That did not work. Try again.'),
    });
  }
}
