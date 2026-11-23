import { DatePipe, DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { NzAlertModule } from 'ng-zorro-antd/alert';
import { NzTableModule } from 'ng-zorro-antd/table';
import { NzTabsModule } from 'ng-zorro-antd/tabs';
import { NzTagModule } from 'ng-zorro-antd/tag';
import { ExposureBar } from '../shared/exposure-bar';
import { FORMAT_PIPES } from '../shared/format';
import { pageData } from '../shared/page-data';
import { Store } from '@ngxs/store';
import { SessionState } from '../core/session.state';
import { ManualBookPanel } from '../manual/manual-book';
import { PerformancePanel } from '../performance/performance-panel';
import { LiquidityPanel } from '../risk/liquidity-panel';
import { RiskPanel } from '../risk/risk-panel';
import { PageHead } from '../shared/page-head';
import { FundsApi } from './funds.api';

@Component({
  selector: 'app-manager-page',
  imports: [DatePipe, DecimalPipe, RouterLink, NzAlertModule, NzTableModule, NzTabsModule, NzTagModule, ExposureBar, PageHead, PerformancePanel, RiskPanel, LiquidityPanel, ManualBookPanel, ...FORMAT_PIPES],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (page.failed()) {
      <nz-alert nzType="error" nzMessage="Could not load the manager." nzShowIcon />
    }
    @if (page.data(); as d) {
      <app-page-head
        [crumbs]="[{ label: d.fund.name, link: ['/funds', d.fund.id] }, { label: d.sleeve.name, link: ['/sleeves', d.sleeve.id] }, { label: d.manager.name }]"
        [title]="d.manager.name"
        [demo]="d.fund.isDemo"
        [sub]="'Manager · ' + d.sleeve.name + ' sleeve · since ' + (d.inceptionDate | date: 'd MMM y' : 'UTC')"
        [asOf]="d.asOf"
      />

      <div class="tiles">
        <div class="tile"><div class="k">Manager NAV</div><div class="v">{{ d.totals.navUsd | usd }}</div><div class="d">{{ d.totals.navUsd | share: d.sleeveNavUsd : 0 }} of sleeve · {{ d.totals.navUsd | share: d.fundNavUsd }} of fund</div></div>
        <div class="tile"><div class="k">Long</div><div class="v">{{ d.totals.longUsd | usd }}</div><div class="d">{{ d.totals.longUsd | share: d.totals.navUsd : 0 }} of NAV</div></div>
        <div class="tile"><div class="k">Short</div><div class="v">{{ d.totals.shortUsd | usd }}</div><div class="d">{{ -d.totals.shortUsd | share: d.totals.navUsd : 0 }} of NAV</div></div>
        <div class="tile"><div class="k">Gross / Net</div><div class="v">{{ d.totals.grossUsd | share: d.totals.navUsd : 0 }} / {{ d.totals.netUsd / d.totals.navUsd | pct: 0 : false }}</div><div class="d">of manager NAV</div></div>
        <div class="tile"><div class="k">Unrealised PnL</div><div class="v" [class]="unrealised() | tone">{{ unrealised() | usd: 2 }}</div><div class="d">Open positions</div></div>
      </div>

      <nz-tabs [nzAnimated]="false" [nzSelectedIndex]="startTab">
        <nz-tab nzTitle="Positions">
      <div class="panel">
        <h3>Positions <span class="hint">Click an instrument to see the asset across the fund</span></h3>
        <nz-table #positions [nzData]="d.positions" [nzShowPagination]="false" [nzFrontPagination]="false" nzSize="small" [nzScroll]="{ x: '1000px' }">
          <thead>
            <tr>
              <th nzWidth="150px">Instrument</th><th>Type</th><th>Side</th><th class="num">Quantity</th><th class="num">Entry</th><th class="num">Mark</th><th class="num">Exposure</th>
              <th class="num">NAV value</th><th class="num">Unrl. PnL</th><th class="num">% manager</th><th class="num">% sleeve</th><th class="num">% fund</th>
            </tr>
          </thead>
          <tbody>
            @for (row of positions.data; track row.instrument) {
              <tr>
                <td class="nowrap">
                  <span class="coin">{{ row.asset }}</span>
                  @if (row.type === 'Cash') {
                    {{ row.instrument }}
                  } @else {
                    <a [routerLink]="['/funds', d.fund.id, 'assets', row.asset]"><b>{{ row.instrument }}</b></a>
                  }
                </td>
                <td><nz-tag>{{ row.type }}</nz-tag></td>
                <td>
                  @if (row.type !== 'Cash') {
                    <nz-tag [nzColor]="row.quantity >= 0 ? 'green' : 'geekblue'">{{ row.quantity >= 0 ? 'Long' : 'Short' }}</nz-tag>
                  }
                </td>
                <td class="num">{{ abs(row.quantity) | number: (abs(row.quantity) >= 1000 ? '1.0-0' : '1.0-4') }}</td>
                <td class="num">{{ row.type === 'Cash' ? '' : (row.entryPrice | price) }}</td>
                <td class="num">{{ row.markPrice | price }}</td>
                <td class="num" [class]="row.exposureUsd | tone">{{ row.type === 'Cash' ? '–' : (row.exposureUsd | usd: 2) }}</td>
                <td class="num">{{ row.marketValueUsd | usd: 2 }}</td>
                <td class="num" [class]="row.unrealizedPnlUsd | tone">{{ row.type === 'Cash' ? '' : (row.unrealizedPnlUsd | usd: 2) }}</td>
                <td class="num">{{ exposureOrValue(row) | share: d.totals.navUsd }}</td>
                <td class="num">{{ exposureOrValue(row) | share: d.sleeveNavUsd }}</td>
                <td class="num">{{ exposureOrValue(row) | share: d.fundNavUsd }}</td>
              </tr>
            }
            <tr class="total-row">
              <td>Total</td><td></td><td></td><td></td><td></td><td></td>
              <td class="num">{{ d.totals.netUsd | usd: 2 }}</td><td class="num">{{ d.totals.navUsd | usd: 2 }}</td><td class="num">{{ unrealised() | usd: 2 }}</td>
              <td class="num">{{ d.totals.grossUsd | share: d.totals.navUsd }}</td><td class="num">{{ d.totals.grossUsd | share: d.sleeveNavUsd }}</td><td class="num">{{ d.totals.grossUsd | share: d.fundNavUsd }}</td>
            </tr>
          </tbody>
        </nz-table>
        <p style="color:#8c8c8c;font-size:12px;margin:8px 0 0">Shares are the position's exposure (its value for cash) against NAV. The total row shows gross exposure.</p>
      </div>

      <div class="panel" style="max-width:720px">
        <h3>Exposure by asset</h3>
        <nz-table #assets [nzData]="d.assets" [nzShowPagination]="false" [nzFrontPagination]="false" nzSize="small">
          <thead><tr><th>Asset</th><th class="num">Long</th><th class="num">Short</th><th class="num">Net</th><th style="width:40%">Short · Long</th></tr></thead>
          <tbody>
            @for (row of assets.data; track row.asset.symbol) {
              <tr>
                <td><span class="coin">{{ row.asset.symbol }}</span>{{ row.asset.symbol }}</td>
                <td class="num">{{ row.longUsd | usd }}</td><td class="num">{{ row.shortUsd | usd }}</td>
                <td class="num" [class]="row.netUsd | tone">{{ row.netUsd | usd }}</td>
                <td><app-exposure-bar [long]="row.longUsd" [short]="row.shortUsd" [scale]="scale()" /></td>
              </tr>
            }
          </tbody>
        </nz-table>
      </div>
        </nz-tab>
        <nz-tab nzTitle="Performance">
          <ng-template nz-tab>
            <app-performance-panel scope="Manager" [scopeId]="d.manager.id" />
          </ng-template>
        </nz-tab>
        <nz-tab nzTitle="Risk">
          <ng-template nz-tab>
            <app-risk-panel scope="Manager" [scopeId]="d.manager.id" />
          </ng-template>
        </nz-tab>
        <nz-tab nzTitle="Liquidity">
          <ng-template nz-tab>
            <app-liquidity-panel scope="Manager" [scopeId]="d.manager.id" />
          </ng-template>
        </nz-tab>
        @if (isAdmin()) {
          <nz-tab nzTitle="Manual positions">
            <ng-template nz-tab>
              <app-manual-book [managerId]="d.manager.id" />
            </ng-template>
          </nz-tab>
        }
      </nz-tabs>
    }
  `,
})
export class ManagerPage {
  private readonly api = inject(FundsApi);
  protected readonly isAdmin = inject(Store).selectSignal(SessionState.isAdmin);
  /** A manager that was just added to be filled in by hand opens on that tab. */
  protected readonly startTab = inject(ActivatedRoute).snapshot.queryParamMap.get('tab') === 'manual' ? 4 : 0;

  protected readonly page = pageData((params, filter) => this.api.manager(Number(params.get('managerId')), filter));
  protected readonly unrealised = computed(() => (this.page.data()?.positions ?? []).reduce((sum, p) => sum + p.unrealizedPnlUsd, 0));
  protected readonly scale = computed(() => Math.max(1, ...(this.page.data()?.assets ?? []).flatMap((a) => [a.longUsd, -a.shortUsd])));
  protected readonly abs = Math.abs;

  protected exposureOrValue(row: { type: string; exposureUsd: number; marketValueUsd: number }): number {
    return row.type === 'Cash' ? row.marketValueUsd : Math.abs(row.exposureUsd);
  }
}
