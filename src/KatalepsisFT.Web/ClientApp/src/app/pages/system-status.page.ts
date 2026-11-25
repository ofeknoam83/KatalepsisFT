import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NzAlertModule } from 'ng-zorro-antd/alert';
import { NzTableModule } from 'ng-zorro-antd/table';
import { NzTagModule } from 'ng-zorro-antd/tag';
import { RouterLink } from '@angular/router';
import { catchError, of, switchMap, timer } from 'rxjs';

interface SystemStatus {
  postgres: string;
  keyValue: string;
  workerHeartbeatAgeSeconds: Record<string, number | null>;
  isReady: boolean;
}

interface Security {
  readKeys: number;
  readKeysConfirmed: number;
  wallets: number;
  paperAccounts: number;
  liveAccounts: number;
  tradingKeys: number;
  strategyKeys: number;
  killSwitchesPulled: number;
  approvalsWaiting: number;
  approvalsDecided: number;
  users: number;
  administrators: number;
  approvesAlone: number;
  auditEntries: number;
  lastAuditEntryAt: string | null;
}

/** One control: what it is, how it stands now, and the rule behind it. */
interface Control {
  name: string;
  now: string;
  rule: string;
  state: 'ok' | 'note';
}

interface Row {
  name: string;
  detail: string;
  state: 'ok' | 'late' | 'down';
}

/** A worker publishes every 10 seconds; after three missed beats it is shown as late. */
const LATE_AFTER_SECONDS = 30;

@Component({
  selector: 'app-system-status-page',
  imports: [NzAlertModule, NzTableModule, NzTagModule, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page-head"><h1>System status</h1><span class="sub">What this web instance can reach right now. Refreshes every 10 seconds.</span></div>

    @if (failed()) {
      <nz-alert nzType="error" nzMessage="Could not load the status. Retrying." nzShowIcon style="display:block;margin-bottom:16px" />
    }

    <nz-table #table [nzData]="rows()" [nzShowPagination]="false" [nzLoading]="rows().length === 0 && !failed()" nzSize="small" class="panel">
      <thead>
        <tr>
          <th>Component</th>
          <th>Detail</th>
          <th>State</th>
        </tr>
      </thead>
      <tbody>
        @for (row of table.data; track row.name) {
          <tr>
            <td>{{ row.name }}</td>
            <td>{{ row.detail }}</td>
            <td>
              @switch (row.state) {
                @case ('ok') {
                  <nz-tag nzColor="success">OK</nz-tag>
                }
                @case ('late') {
                  <nz-tag nzColor="warning">Late</nz-tag>
                }
                @default {
                  <nz-tag nzColor="error">Not answering</nz-tag>
                }
              }
            </td>
          </tr>
        }
      </tbody>
    </nz-table>

    @if (controls().length > 0) {
      <div class="panel" style="margin-top:18px" data-testid="security">
        <h3>Security <span class="hint">How the controls that protect money stand right now, counted from the database. No key is ever shown.</span></h3>
        <nz-table #security [nzData]="controls()" [nzShowPagination]="false" [nzFrontPagination]="false" nzSize="small">
          <thead>
            <tr><th style="width:20%">Control</th><th style="width:30%">Now</th><th>The rule</th><th style="width:100px">State</th></tr>
          </thead>
          <tbody>
            @for (row of security.data; track row.name) {
              <tr>
                <td><b>{{ row.name }}</b></td>
                <td>{{ row.now }}</td>
                <td style="color:var(--kft-text-2)">{{ row.rule }}</td>
                <td>
                  @if (row.state === 'ok') {
                    <nz-tag nzColor="success">In force</nz-tag>
                  } @else {
                    <nz-tag nzColor="warning">Attention</nz-tag>
                  }
                </td>
              </tr>
            }
          </tbody>
        </nz-table>
        <p style="color:var(--kft-text-3);font-size:12px;margin:10px 0 0">Every change to any of these is written to the <a routerLink="/admin/audit">audit trail</a>.</p>
      </div>
    }
  `,
})
export class SystemStatusPage {
  private readonly http = inject(HttpClient);

  protected readonly rows = signal<Row[]>([]);
  protected readonly failed = signal(false);
  protected readonly controls = signal<Control[]>([]);

  constructor() {
    timer(0, 10_000)
      .pipe(
        switchMap(() => this.http.get<SystemStatus>('/api/system/status').pipe(catchError(() => of(null)))),
        takeUntilDestroyed(),
      )
      .subscribe((status) => {
        this.http
          .get<Security>('/api/system/security')
          .pipe(catchError(() => of(null)))
          .subscribe((s) => {
            if (s !== null) {
              this.controls.set(toControls(s));
            }
          });
        this.failed.set(status === null);
        if (status !== null) {
          this.rows.set(toRows(status));
        }
      });
  }
}

const count = (n: number, one: string, many: string): string => `${n} ${n === 1 ? one : many}`;

function toControls(s: Security): Control[] {
  const unconfirmed = s.readKeys - s.readKeysConfirmed;
  const lastWritten = s.lastAuditEntryAt === null ? 'nothing yet' : s.lastAuditEntryAt.slice(0, 16).replace('T', ' ') + ' UTC';
  return [
    {
      name: 'Keys for watching',
      now: `${count(s.readKeys, 'key', 'keys')}, ${s.readKeysConfirmed} confirmed read-only · ${count(s.wallets, 'wallet', 'wallets')} by address`,
      rule: 'A key that can trade or withdraw is refused. Where a venue cannot tell us, the key is kept and marked as not confirmed.',
      state: unconfirmed > 0 ? 'note' : 'ok',
    },
    {
      name: 'Keys for trading',
      now: `${count(s.tradingKeys, 'sealed key', 'sealed keys')} · ${count(s.liveAccounts, 'account', 'accounts')} on real money, ${s.paperAccounts} on paper`,
      rule: 'A trading key must be able to trade and must not be able to withdraw. It is sealed so that only the execution service can open it; this web application cannot.',
      state: 'ok',
    },
    {
      name: 'Keys for programs',
      now: count(s.strategyKeys, 'strategy key switched on', 'strategy keys switched on'),
      rule: 'Only a fingerprint of the key is stored. Each key is tied to named accounts and has its own largest order.',
      state: 'ok',
    },
    {
      name: 'Kill switch',
      now: s.killSwitchesPulled === 0 ? 'None pulled; trading is allowed' : `${s.killSwitchesPulled} pulled; trading is stopped there`,
      rule: 'One person can pull it. It cancels open orders and refuses new ones. Releasing it needs approval.',
      state: s.killSwitchesPulled > 0 ? 'note' : 'ok',
    },
    {
      name: 'Approvals',
      now: `${s.approvalsWaiting} waiting · ${s.approvalsDecided} decided so far`,
      rule: 'A trading key, higher limits, real money and a very large order take effect only after approval. Nobody approves their own request' + (s.approvesAlone > 0 ? `, except ${count(s.approvesAlone, 'administrator', 'administrators')} allowed to from the server, and that is marked on the record.` : '.'),
      state: s.approvalsWaiting > 0 ? 'note' : 'ok',
    },
    {
      name: 'People',
      now: `${count(s.users, 'user', 'users')}, ${count(s.administrators, 'administrator', 'administrators')}`,
      rule: 'Three roles, checked on the server for every request. An account locks after repeated wrong passwords. The last administrator cannot be removed.',
      state: 'ok',
    },
    {
      name: 'The record',
      now: `${s.auditEntries.toLocaleString('en-US')} entries · last written ${lastWritten}`,
      rule: 'Every change is written with who made it and when. Entries are only ever added; no screen edits or deletes one.',
      state: 'ok',
    },
  ];
}

function toRows(status: SystemStatus): Row[] {
  const store = (name: string, value: string): Row => ({ name, detail: value, state: value === 'ok' ? 'ok' : 'down' });
  const workers = Object.entries(status.workerHeartbeatAgeSeconds).map(([group, age]): Row => {
    const name = `Worker: ${group}`;
    if (age === null) {
      return { name, detail: 'No heartbeat received by this instance yet', state: 'down' };
    }

    return { name, detail: `Last heartbeat ${Math.round(age)} s ago`, state: age > LATE_AFTER_SECONDS ? 'late' : 'ok' };
  });
  return [store('Postgres', status.postgres), store('Key Value', status.keyValue), ...workers];
}
