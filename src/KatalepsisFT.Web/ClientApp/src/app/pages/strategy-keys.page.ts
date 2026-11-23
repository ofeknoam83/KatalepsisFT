import { DatePipe } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { toObservable } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { Store } from '@ngxs/store';
import { NzAlertModule } from 'ng-zorro-antd/alert';
import { NzButtonModule } from 'ng-zorro-antd/button';
import { NzInputModule } from 'ng-zorro-antd/input';
import { NzInputNumberModule } from 'ng-zorro-antd/input-number';
import { NzSelectModule } from 'ng-zorro-antd/select';
import { NzTableModule } from 'ng-zorro-antd/table';
import { NzTagModule } from 'ng-zorro-antd/tag';
import { Observable, catchError, forkJoin, map, of, switchMap } from 'rxjs';
import { ViewState } from '../funds/view.state';
import { FORMAT_PIPES } from '../shared/format';
import { keptData } from '../shared/page-data';
import { Trading } from '../trading/models';

interface StrategyKey {
  id: number;
  name: string;
  prefix: string;
  accountIds: number[];
  maxOrderNotionalUsd: number;
  isEnabled: boolean;
  createdBy: string;
  createdAt: string;
  lastUsedAt: string | null;
}

/** Keys with which a strategy sends orders by itself, each tied to the trading accounts it may use. */
@Component({
  selector: 'app-strategy-keys-page',
  imports: [DatePipe, FormsModule, NzAlertModule, NzButtonModule, NzInputModule, NzInputNumberModule, NzSelectModule, NzTableModule, NzTagModule, ...FORMAT_PIPES],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page-head"><h1>Strategy keys</h1><span class="sub">For strategies that send their own orders</span></div>
    @if (error(); as message) {
      <nz-alert nzType="error" [nzMessage]="message" nzShowIcon nzCloseable (nzOnClose)="error.set(null)" style="display:block;margin-bottom:10px" />
    }
    @if (secret(); as s) {
      <nz-alert nzType="success" nzShowIcon nzCloseable (nzOnClose)="secret.set(null)" style="display:block;margin-bottom:10px" data-testid="strategy-secret"
        [nzMessage]="'Key for ' + s.name + ': ' + s.secret"
        nzDescription="It is shown this once and cannot be read again; only a fingerprint of it is kept. Give it to the strategy as a secret." />
    }
    @if (loaded.data(); as d) {
      <div class="panel">
        <nz-table [nzData]="d.keys" nzTemplateMode [nzShowPagination]="false" nzSize="small" data-testid="strategy-keys">
          <thead>
            <tr><th>Name</th><th>Key</th><th>May trade</th><th class="num">Largest order</th><th>Last used</th><th></th></tr>
          </thead>
          <tbody>
            @for (k of d.keys; track k.id) {
              <tr>
                <td><b>{{ k.name }}</b>
                  @if (!k.isEnabled) {
                    <nz-tag style="margin-left:6px">Switched off</nz-tag>
                  }
                </td>
                <td><code>{{ k.prefix }}…</code></td>
                <td>{{ names(d.accounts, k.accountIds) }}</td>
                <td class="num">{{ k.maxOrderNotionalUsd | usd }}</td>
                <td class="nowrap">{{ k.lastUsedAt ? (k.lastUsedAt | date: 'd MMM y HH:mm' : 'UTC') + ' UTC' : 'Never' }}</td>
                <td class="nowrap" style="text-align:right"><a (click)="run(http.post('/api/strategy-keys/' + k.id + '/enabled', { enabled: !k.isEnabled }))">{{ k.isEnabled ? 'Switch off' : 'Switch on' }}</a></td>
              </tr>
            }
            @if (d.keys.length === 0) {
              <tr><td colspan="6" style="text-align:center;color:#8c8c8c;padding:22px">No strategy has a key yet.</td></tr>
            }
          </tbody>
        </nz-table>
        <div class="add">
          <input nz-input [ngModel]="name()" (ngModelChange)="name.set($event)" placeholder="Strategy name" maxlength="100" style="width:200px" data-testid="strategy-name" />
          <nz-select nzMode="multiple" [ngModel]="accounts()" (ngModelChange)="accounts.set($event)" nzPlaceHolder="Trading accounts it may use" style="flex:1;min-width:280px" data-testid="strategy-accounts">
            @for (a of d.accounts; track a.id) {
              <nz-option [nzValue]="a.id" [nzLabel]="a.label" />
            }
          </nz-select>
          <nz-input-number [ngModel]="maxOrder()" (ngModelChange)="maxOrder.set($event)" [nzMin]="1" [nzStep]="10000" nzPlaceHolder="Largest order, USD" style="width:180px" />
          <button nz-button nzType="primary" [disabled]="name().trim() === '' || accounts().length === 0 || !maxOrder()" (click)="add()" data-testid="strategy-add">Create a key</button>
        </div>
        @if (d.accounts.length === 0) {
          <p class="note">There is no trading account yet. Add one on a fund's Trading tab first.</p>
        }
      </div>
      <div class="panel">
        <h3>How a strategy uses its key</h3>
        <p class="note" style="font-size:13px">
          It sends the key in the header <code>X-KFT-Strategy-Key</code>. <code>POST /api/strategy/orders</code> places an order with the same fields as the order ticket
          (<code>tradingAccountId</code> or <code>managerId</code>, <code>asset</code>, <code>instrumentType: "Perp"</code>, <code>side</code>, <code>type</code>, <code>timeInForce</code>, <code>quantity</code>, <code>limitPrice</code>, <code>reduceOnly</code>, <code>clientOrderId</code>).
          Sending the same <code>clientOrderId</code> again returns the same order, so a retry after a lost answer never trades twice.
          <code>GET /api/strategy/orders/{{ '{' }}id{{ '}' }}</code> reads it back, <code>POST /api/strategy/orders/{{ '{' }}id{{ '}' }}/cancel</code> cancels it, and <code>GET /api/strategy/book/Perp/BTC</code> gives the consolidated book.
          Every order passes the same checks as one from the ticket, plus this key's own largest order.
        </p>
      </div>
    }
  `,
  styles: `
    .add {
      display: flex;
      gap: 8px;
      flex-wrap: wrap;
      margin-top: 12px;
    }

    .note {
      color: #8c8c8c;
      font-size: 12px;
      margin: 8px 0 0;
    }
  `,
})
export class StrategyKeysPage {
  protected readonly http = inject(HttpClient);
  private readonly funds = inject(Store).selectSignal(ViewState.funds);
  private readonly fundIds = computed(() => (this.funds() ?? []).filter((f) => !f.isDemo).map((f) => f.id).join(','));

  protected readonly error = signal<string | null>(null);
  protected readonly secret = signal<{ name: string; secret: string } | null>(null);
  private readonly version = signal(0);
  protected readonly loaded = keptData(toObservable(this.fundIds), toObservable(this.version), (ids) =>
    this.http.get<StrategyKey[]>('/api/strategy-keys').pipe(
      switchMap((keys) => {
        const each = ids === '' ? [] : ids.split(',').map((id) => this.http.get<Trading>(`/api/funds/${id}/trading`).pipe(catchError(() => of(null))));
        return (each.length === 0 ? of([] as (Trading | null)[]) : forkJoin(each)).pipe(
          map((funds) => ({ keys, accounts: funds.flatMap((f) => (f === null ? [] : f.accounts.map((a) => ({ id: a.id, label: `${f.fundName} · ${a.manager} · ${a.venueName}` })))) })),
        );
      }),
    ),
  );

  protected readonly name = signal('');
  protected readonly accounts = signal<number[]>([]);
  protected readonly maxOrder = signal<number | null>(100_000);

  protected names(all: { id: number; label: string }[], ids: number[]): string {
    return ids.map((id) => all.find((a) => a.id === id)?.label ?? `account ${id}`).join('; ');
  }

  protected add(): void {
    const name = this.name().trim();
    this.run(this.http.post<{ value: { secret: string } }>('/api/strategy-keys', { name, accountIds: this.accounts(), maxOrderNotionalUsd: this.maxOrder() }), (created) => {
      this.secret.set({ name, secret: created.value.secret });
      this.name.set('');
      this.accounts.set([]);
    });
  }

  protected run<T>(request: Observable<T>, done?: (answer: T) => void): void {
    this.error.set(null);
    request.subscribe({
      next: (answer) => {
        done?.(answer);
        this.version.update((v) => v + 1);
      },
      error: (e: { error?: { message?: string } }) => this.error.set(e.error?.message ?? 'That did not work. Try again.'),
    });
  }
}
