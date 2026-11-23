import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
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
import { LiveService } from '../core/live.service';
import { Observable, catchError, combineLatest, of, switchMap } from 'rxjs';
import { toObservable } from '@angular/core/rxjs-interop';
import { ConnectionsApi, Venue } from '../connections/connections.api';
import { FORMAT_PIPES } from '../shared/format';
import { pageData } from '../shared/page-data';
import { PageHead } from '../shared/page-head';
import { FundTabs } from './fund-tabs';
import { FundsApi } from './funds.api';

interface KeyForm {
  managerId: FormControl<number | null>;
  venue: FormControl<Venue | null>;
  label: FormControl<string>;
  apiKey: FormControl<string>;
  apiSecret: FormControl<string>;
  passphrase: FormControl<string>;
}

const STATE_COLOR = { Live: 'green', Failed: 'red', Pending: 'blue', Disabled: 'default' } as const;

/** The read-only venue keys a fund's managers are read through. Admin only. */
@Component({
  selector: 'app-fund-connections-page',
  imports: [DatePipe, ReactiveFormsModule, NzAlertModule, NzButtonModule, NzFormModule, NzInputModule, NzModalModule, NzPopconfirmModule, NzSelectModule, NzTableModule, NzTagModule, PageHead, FundTabs, ...FORMAT_PIPES],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (page.data(); as d) {
      <app-page-head [crumbs]="[{ label: d.fund.name, link: ['/funds', d.fund.id] }, { label: 'Connections' }]" [title]="d.fund.name" [demo]="d.fund.isDemo" sub="Read-only venue keys" [showFilter]="false" />
      <app-fund-tabs [fundId]="d.fund.id" />
      @if (error(); as message) {
        <nz-alert nzType="error" [nzMessage]="message" nzShowIcon nzCloseable (nzOnClose)="error.set(null)" style="display:block;margin-bottom:14px" />
      }
      @if (data(); as c) {
        @if (c.managers.length === 0) {
          <nz-alert nzType="info" nzShowIcon nzMessage="This fund's positions do not come from venues, so it has no connections." style="display:block;margin-bottom:14px" />
        } @else if (!c.canAddKeys) {
          <nz-alert nzType="warning" nzShowIcon nzMessage="The master key is not configured on the server, so keys cannot be stored yet." style="display:block;margin-bottom:14px" />
        }
        <div class="panel">
          <h3>
            Connections
            <span class="hint">Positions are read about once a minute, deposits and withdrawals every ten. A key that can trade or withdraw is refused.</span>
            <button nz-button nzType="primary" nzSize="small" style="margin-left:12px" [disabled]="!c.canAddKeys || c.managers.length === 0" (click)="open()" data-testid="add-key">Add a key</button>
          </h3>
          <nz-table [nzData]="c.connections" nzTemplateMode [nzShowPagination]="false" nzSize="small" data-testid="connections-table">
            <thead>
              <tr><th>Manager</th><th>Venue</th><th>Label</th><th>Key</th><th>State</th><th>Last read</th><th class="num">Positions</th><th class="num">Value from positions</th><th class="num">Value the venue states</th><th></th></tr>
            </thead>
            <tbody>
              @for (row of c.connections; track row.id) {
                <tr>
                  <td><b>{{ row.manager }}</b> <span style="color:#8c8c8c">· {{ row.sleeve }}</span></td>
                  <td>{{ row.venueName }}</td>
                  <td>{{ row.label }}</td>
                  <td class="nowrap">…{{ row.keyHint }} @if (!row.permissionsVerified) {<nz-tag nzColor="orange">Permissions unverified</nz-tag>}</td>
                  <td>
                    <nz-tag [nzColor]="stateColor[row.state]">{{ row.state }}</nz-tag>
                    @if (row.lastError) {
                      <div style="color:#cf1322;font-size:12px;max-width:320px">{{ row.lastError }}</div>
                    }
                    @for (note of row.notes; track note) {
                      <div style="color:#ad6800;font-size:12px;max-width:320px">{{ note }}</div>
                    }
                    @if (row.transfersError) {
                      <div style="color:#ad6800;font-size:12px;max-width:320px">Deposits and withdrawals are not being read: {{ row.transfersError }}</div>
                    }
                  </td>
                  <td class="nowrap">{{ row.lastSuccessAt ? (row.lastSuccessAt | date: 'MM-dd-yyyy HH:mm' : 'UTC') : 'Never' }}</td>
                  <td class="num">{{ row.positions }}</td>
                  <td class="num">{{ row.computedNavUsd | usd: 2 }}</td>
                  <td class="num" [class.down]="gap(row.computedNavUsd, row.reportedEquityUsd)">{{ row.reportedEquityUsd | usd: 2 }}</td>
                  <td class="nowrap">
                    @if (row.state !== 'Disabled') {
                      <a (click)="act(api.sync(row.id))">Read now</a>
                      <a style="margin-left:12px" (click)="act(api.setEnabled(row.id, false))">Switch off</a>
                    } @else {
                      <a (click)="act(api.setEnabled(row.id, true))">Switch on</a>
                    }
                    <a style="margin-left:12px;color:#cf1322" nz-popconfirm nzPopconfirmTitle="Remove this key? Its positions leave the manager's book; history stays." nzOkText="Remove" (nzOnConfirm)="act(api.remove(row.id))">Remove</a>
                  </td>
                </tr>
              }
              @if (c.connections.length === 0) {
                <tr><td colspan="10" style="text-align:center;color:#8c8c8c;padding:28px">No keys yet.</td></tr>
              }
            </tbody>
          </nz-table>
          <p style="color:#8c8c8c;font-size:12px;margin:10px 0 0">The two values should be close. A red figure means they differ by more than 1%, so part of the account is not being read.</p>
        </div>

        <nz-modal [nzVisible]="adding()" nzTitle="Add a read-only key" nzOkText="Check and save" [nzOkLoading]="saving()" [nzOkDisabled]="form.invalid" (nzOnOk)="submit()" (nzOnCancel)="adding.set(false)">
          <ng-container *nzModalContent>
            <form nz-form nzLayout="vertical" [formGroup]="form" autocomplete="off">
              @if (formError(); as message) {
                <nz-alert nzType="error" [nzMessage]="message" nzShowIcon style="display:block;margin-bottom:14px" />
              }
              <div style="display:grid;grid-template-columns:1fr 1fr;gap:12px">
                <nz-form-item>
                  <nz-form-label>Manager</nz-form-label>
                  <nz-select formControlName="managerId" data-testid="key-manager">
                    @for (m of c.managers; track m.id) {
                      <nz-option [nzValue]="m.id" [nzLabel]="m.name + ' · ' + m.sleeve" />
                    }
                  </nz-select>
                </nz-form-item>
                <nz-form-item>
                  <nz-form-label>Venue</nz-form-label>
                  <nz-select formControlName="venue" data-testid="key-venue">
                    @for (v of c.venues; track v.venue) {
                      <nz-option [nzValue]="v.venue" [nzLabel]="v.name" />
                    }
                  </nz-select>
                </nz-form-item>
              </div>
              <nz-form-item><nz-form-label>Label</nz-form-label><input nz-input formControlName="label" placeholder="Main account" maxlength="100" /></nz-form-item>
              <nz-form-item><nz-form-label>API key</nz-form-label><input nz-input formControlName="apiKey" autocomplete="off" data-testid="key-api-key" /></nz-form-item>
              <nz-form-item><nz-form-label>API secret</nz-form-label><input nz-input type="password" formControlName="apiSecret" autocomplete="new-password" data-testid="key-api-secret" /></nz-form-item>
              @if (needsPassphrase()) {
                <nz-form-item><nz-form-label>Passphrase</nz-form-label><input nz-input type="password" formControlName="passphrase" autocomplete="new-password" /></nz-form-item>
              }
              <p style="color:#8c8c8c;font-size:12px;margin:0">The key is checked with the venue before it is saved, stored encrypted, and never shown again.</p>
            </form>
          </ng-container>
        </nz-modal>
      }
    }
  `,
})
export class FundConnectionsPage {
  protected readonly api = inject(ConnectionsApi);
  private readonly funds = inject(FundsApi);

  protected readonly stateColor = STATE_COLOR;
  protected readonly page = pageData((params) => this.funds.overview(Number(params.get('fundId')), 'All'));
  protected readonly error = signal<string | null>(null);
  protected readonly formError = signal<string | null>(null);
  protected readonly adding = signal(false);
  protected readonly saving = signal(false);
  private readonly version = signal(0);
  private readonly fundId = computed(() => this.page.data()?.fund.id ?? null);

  protected readonly data = toSignal(
    combineLatest([toObservable(this.fundId), toObservable(this.version), toObservable(inject(LiveService).tick)]).pipe(switchMap(([fundId]) => (fundId === null ? of(null) : this.api.list(fundId).pipe(catchError(() => of(null)))))),
    { initialValue: null },
  );

  protected readonly form = new FormGroup<KeyForm>({
    managerId: new FormControl<number | null>(null, { validators: [Validators.required] }),
    venue: new FormControl<Venue | null>(null, { validators: [Validators.required] }),
    label: new FormControl('Main account', { nonNullable: true, validators: [Validators.required] }),
    apiKey: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    apiSecret: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    passphrase: new FormControl('', { nonNullable: true }),
  });
  private readonly venue = toSignal(this.form.controls.venue.valueChanges, { initialValue: null });
  protected readonly needsPassphrase = computed(() => this.data()?.venues.find((v) => v.venue === this.venue())?.needsPassphrase ?? false);

  protected open(): void {
    const c = this.data();
    this.form.reset({ managerId: c?.managers[0]?.id ?? null, venue: c?.venues[0]?.venue ?? null, label: 'Main account', apiKey: '', apiSecret: '', passphrase: '' });
    this.formError.set(null);
    this.adding.set(true);
  }

  protected submit(): void {
    const value = this.form.getRawValue();
    this.saving.set(true);
    this.formError.set(null);
    this.api
      .add({ managerId: value.managerId!, venue: value.venue!, label: value.label, apiKey: value.apiKey, apiSecret: value.apiSecret, passphrase: value.passphrase || null })
      .subscribe({
        next: () => {
          this.saving.set(false);
          this.adding.set(false);
          this.form.reset();
          this.version.update((v) => v + 1);
        },
        error: (e: { error?: { message?: string } }) => {
          this.saving.set(false);
          this.formError.set(e.error?.message ?? 'The key could not be saved.');
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

  /** True when the venue's own figure and ours differ by more than 1%. */
  protected gap(computed: number | null, reported: number | null): boolean {
    return computed !== null && reported !== null && Math.abs(computed - reported) > 0.01 * Math.max(Math.abs(reported), 1);
  }
}
