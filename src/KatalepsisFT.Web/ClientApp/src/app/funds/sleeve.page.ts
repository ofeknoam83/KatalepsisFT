import { DecimalPipe } from '@angular/common';
import { Coin } from '../shared/coin';
import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { NzAlertModule } from 'ng-zorro-antd/alert';
import { NzTableModule } from 'ng-zorro-antd/table';
import { NzTabsModule } from 'ng-zorro-antd/tabs';
import { NzTagModule } from 'ng-zorro-antd/tag';
import { ExposureBar } from '../shared/exposure-bar';
import { FORMAT_PIPES, pct, wholeUsd } from '../shared/format';
import { AddManager } from './add-manager';
import { pageData } from '../shared/page-data';
import { AlertStrip } from '../alerts/alert-strip';
import { PerformancePanel } from '../performance/performance-panel';
import { LiquidityPanel } from '../risk/liquidity-panel';
import { RiskPanel } from '../risk/risk-panel';
import { PageHead } from '../shared/page-head';
import { FundsApi } from './funds.api';
import { AssetRow, MandateLimit, ManagerRow, SleeveDetail } from './models';

interface MandateCheck {
  rule: string;
  current: string;
  limit: string;
  ok: boolean;
}

/** Diverging fill for a signed exposure: blue long, red short, darker for larger, on a neutral ground. */
function heat(value: number, max: number): Record<string, string> {
  const t = max > 0 ? Math.max(-1, Math.min(1, value / max)) : 0;
  const to = t >= 0 ? [42, 120, 214] : [208, 59, 59];
  const rgb = [240, 239, 236].map((c, i) => Math.round(c + (to[i] - c) * Math.abs(t)));
  return { background: `rgb(${rgb.join(',')})`, color: Math.abs(t) > 0.5 ? '#fff' : '#1f1f1f' };
}

@Component({
  selector: 'app-sleeve-page',
  imports: [Coin, DecimalPipe, RouterLink, NzAlertModule, NzTableModule, NzTabsModule, NzTagModule, ExposureBar, PageHead, AlertStrip, AddManager, PerformancePanel, RiskPanel, LiquidityPanel, ...FORMAT_PIPES],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (page.failed()) {
      <nz-alert nzType="error" nzMessage="Could not load the sleeve." nzShowIcon />
    }
    @if (page.data(); as d) {
      <app-page-head
        [crumbs]="[{ label: d.fund.name, link: ['/funds', d.fund.id] }, { label: d.sleeve.name }]"
        [title]="d.sleeve.name" [renameUrl]="'/api/sleeves/' + d.sleeve.id + '/name'" renameWhat="the sleeve"
        [demo]="d.fund.isDemo"
        [sub]="'Sleeve · ' + (d.sleeve.mandateType | label) + ' · ' + d.managers.length + ' managers'"
        [asOf]="d.asOf"
      />

      <div class="tiles">
        <div class="tile"><div class="k">Sleeve NAV</div><div class="v">{{ d.totals.navUsd | compact }}</div><div class="x">{{ d.totals.navUsd | usd }}</div><div class="d">{{ d.totals.navUsd | share: d.fundNavUsd : 0 }} of fund</div></div>
        <div class="tile"><div class="k">Long</div><div class="v">{{ d.totals.longUsd | compact }}</div><div class="x">{{ d.totals.longUsd | usd }}</div><div class="d">{{ d.totals.longUsd | share: d.totals.navUsd : 0 }} of sleeve NAV</div></div>
        <div class="tile"><div class="k">Short</div><div class="v">{{ d.totals.shortUsd | compact }}</div><div class="x">{{ d.totals.shortUsd | usd }}</div><div class="d">{{ -d.totals.shortUsd | share: d.totals.navUsd : 0 }} of sleeve NAV</div></div>
        <div class="tile"><div class="k">Gross exposure</div><div class="v">{{ d.totals.grossUsd | share: d.totals.navUsd : 0 }}</div><div class="d">{{ d.totals.grossUsd | usd }}</div></div>
        <div class="tile"><div class="k">Net exposure</div><div class="v">{{ d.totals.netUsd / d.totals.navUsd | pct: 0 : false }}</div><div class="d">{{ d.totals.netUsd | usd }}</div></div>
      </div>

      <app-alert-strip [fundId]="d.fund.id" [sleeveId]="d.sleeve.id" />

      <nz-tabs [nzAnimated]="false">
        <nz-tab nzTitle="Managers">
          <div class="panel">
            <h3>
              Managers in this sleeve
              <span class="hint">Click a manager to see positions</span>
              @if (!d.fund.isDemo) {
                <kft-add-manager [fundId]="d.fund.id" [sleeveId]="d.sleeve.id" style="margin-left:12px" />
              }
            </h3>
            <nz-table #managers [nzData]="d.managers" [nzShowPagination]="false" [nzFrontPagination]="false" nzSize="small">
              <thead>
                <tr>
                  <th>Manager</th><th>Data</th><th class="num">NAV</th><th class="num">% of sleeve</th><th class="num">% of fund</th><th class="num">Long</th>
                  <th class="num">Short</th><th class="num">Gross % NAV</th><th class="num">Net % NAV</th><th style="width:22%">Short · Long</th>
                </tr>
              </thead>
              <tbody>
                @for (row of managers.data; track row.manager.id) {
                  <tr>
                    <td>
                      <a [routerLink]="['/managers', row.manager.id]"><b>{{ row.manager.name }}</b></a>
                      @if (row.manager.retiredOn) {
                        <nz-tag style="margin-left:8px">Retired {{ row.manager.retiredOn }}</nz-tag>
                      }
                    </td>
                    <td><nz-tag>{{ row.dataSource }}</nz-tag></td>
                    <td class="num">{{ row.totals.navUsd | usd }}</td>
                    <td class="num">{{ row.totals.navUsd | share: d.totals.navUsd : 0 }}</td>
                    <td class="num">{{ row.totals.navUsd | share: d.fundNavUsd }}</td>
                    <td class="num">{{ row.totals.longUsd | usd }}</td>
                    <td class="num">{{ row.totals.shortUsd | usd }}</td>
                    <td class="num">{{ row.totals.grossUsd | share: row.totals.navUsd : 0 }}</td>
                    <td class="num">{{ row.totals.netUsd / row.totals.navUsd | pct: 0 : false }}</td>
                    <td><app-exposure-bar [long]="row.totals.longUsd" [short]="row.totals.shortUsd" [scale]="managerScale()" /></td>
                  </tr>
                }
              </tbody>
            </nz-table>
          </div>
          <div class="cols">
            <div class="panel">
              <h3>Mandate</h3>
              <nz-table #checks [nzData]="mandate()" [nzShowPagination]="false" [nzFrontPagination]="false" nzSize="small">
                <thead><tr><th>Rule</th><th class="num">Current</th><th class="num">Limit</th><th>Status</th></tr></thead>
                <tbody>
                  @for (check of checks.data; track check.rule) {
                    <tr>
                      <td>{{ check.rule }}</td><td class="num">{{ check.current }}</td><td class="num">{{ check.limit }}</td>
                      <td>
                        @if (check.ok) {
                          <nz-tag nzColor="success">Within limit</nz-tag>
                        } @else {
                          <nz-tag nzColor="error">Breach</nz-tag>
                        }
                      </td>
                    </tr>
                  }
                </tbody>
              </nz-table>
              <p style="color:#8c8c8c;font-size:12px;margin:8px 0 0">Checked on positions. Limits on drawdown and time to liquidate are checked on the performance and liquidity pages.</p>
            </div>
            <div class="panel">
              <h3>By instrument type</h3>
              <nz-table #types [nzData]="d.byType" [nzShowPagination]="false" [nzFrontPagination]="false" nzSize="small">
                <thead><tr><th>Type</th><th class="num">Long</th><th class="num">Short</th><th class="num">Gross</th><th class="num">Net</th></tr></thead>
                <tbody>
                  @for (row of types.data; track row.type) {
                    <tr>
                      <td>{{ row.type }}</td><td class="num">{{ row.longUsd | usd }}</td><td class="num">{{ row.shortUsd | usd }}</td>
                      <td class="num">{{ row.grossUsd | usd }}</td><td class="num" [class]="row.netUsd | tone">{{ row.netUsd | usd }}</td>
                    </tr>
                  }
                </tbody>
              </nz-table>
            </div>
          </div>
        </nz-tab>

        <nz-tab nzTitle="Assets">
          <div class="panel">
            <h3>Asset exposure in {{ d.sleeve.name }} <span class="hint">▲ long · ▼ short · click an asset to see it across all sleeves</span></h3>
            <nz-table #assets [nzData]="d.assets" [nzShowPagination]="false" [nzFrontPagination]="false" nzSize="small">
              <thead>
                <tr>
                  <th>Asset</th><th class="num">Price</th><th class="num">30D</th><th class="num">YTD</th><th>Held by</th><th class="num">Long</th><th class="num">Short</th>
                  <th class="num">Gross</th><th class="num">Net</th><th class="num">% of sleeve</th><th class="num">% of fund</th><th>Overlap</th>
                </tr>
              </thead>
              <tbody>
                @for (row of assets.data; track row.asset.symbol) {
                  <tr>
                    <td class="nowrap"><app-coin [symbol]="row.asset.symbol" /><a [routerLink]="['/funds', d.fund.id, 'assets', row.asset.symbol]"><b>{{ row.asset.symbol }}</b></a></td>
                    <td class="num">{{ row.asset.price | price }}</td>
                    <td class="num" [class]="row.asset.change30D | tone">{{ row.asset.change30D | pct }}</td>
                    <td class="num" [class]="row.asset.changeYtd | tone">{{ row.asset.changeYtd | pct }}</td>
                    <td>
                      @for (holder of row.holders; track holder.managerId) {
                        <nz-tag [nzColor]="holder.netUsd > 0 ? 'green' : holder.netUsd < 0 ? 'geekblue' : 'default'">
                          {{ holder.netUsd > 0 ? '▲' : holder.netUsd < 0 ? '▼' : '◆' }} {{ firstWord(holder.manager) }}
                        </nz-tag>
                      }
                    </td>
                    <td class="num">{{ row.longUsd | usd }}</td>
                    <td class="num">{{ row.shortUsd | usd }}</td>
                    <td class="num">{{ row.grossUsd | usd }}</td>
                    <td class="num" [class]="row.netUsd | tone">{{ row.netUsd | usd }}</td>
                    <td class="num">{{ row.grossUsd | share: d.totals.navUsd }}</td>
                    <td class="num">{{ row.grossUsd | share: d.fundNavUsd }}</td>
                    <td>
                      @if (row.holders.length === 1) {
                        <nz-tag>Unique</nz-tag>
                      } @else {
                        <nz-tag nzColor="blue">Shared ×{{ row.holders.length }}</nz-tag>
                      }
                      @if (row.hasOffsettingManagers) {
                        <nz-tag nzColor="gold">Both sides</nz-tag>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </nz-table>
            <p style="color:#8c8c8c;font-size:12px;margin:8px 0 0">◆ a manager holding both sides of the asset, with no net exposure.</p>
          </div>
        </nz-tab>

        <nz-tab nzTitle="Overlap">
          <div class="panel" style="overflow-x:auto">
            <h3>Who holds what <span class="hint">Net exposure per manager and asset, USD · darker = larger</span></h3>
            <table class="matrix">
              <tr>
                <th></th>
                @for (asset of d.assets; track asset.asset.symbol) {
                  <th>{{ asset.asset.symbol }}</th>
                }
              </tr>
              @for (manager of d.managers; track manager.manager.id) {
                <tr>
                  <th class="row">{{ manager.manager.name }}</th>
                  @for (asset of d.assets; track asset.asset.symbol) {
                    @let value = net(asset, manager.manager.id);
                    @if (value === null) {
                      <td style="background:#fafafa;color:#bfbfbf">·</td>
                    } @else {
                      <td [style]="heatStyle(value)">{{ value >= 0 ? '+' : '−' }}{{ whole(value) }}</td>
                    }
                  }
                </tr>
              }
              <tr>
                <th class="row">Gross</th>
                @for (asset of d.assets; track asset.asset.symbol) {
                  <td><b>{{ whole(asset.grossUsd) }}</b></td>
                }
              </tr>
              <tr>
                <th class="row">% of sleeve</th>
                @for (asset of d.assets; track asset.asset.symbol) {
                  <td>{{ asset.grossUsd | share: d.totals.navUsd : 0 }}</td>
                }
              </tr>
            </table>
            <div style="margin-top:8px;font-size:12px;color:#595959">
              <span class="swatch" style="background:#2a78d6"></span>Long <span class="swatch" style="background:#d03b3b;margin-left:12px"></span>Short
            </div>
          </div>
          <div class="panel" style="overflow-x:auto">
            <h3>Manager-to-manager overlap <span class="hint">Exposure in shared assets ÷ the smaller book · ⇄ on opposite sides of an asset</span></h3>
            <table class="matrix">
              <tr>
                <th></th>
                @for (manager of d.managers; track manager.manager.id) {
                  <th>{{ firstWord(manager.manager.name) }}</th>
                }
              </tr>
              @for (a of d.managers; track a.manager.id) {
                <tr>
                  <th class="row">{{ a.manager.name }}</th>
                  @for (b of d.managers; track b.manager.id) {
                    @if (a.manager.id === b.manager.id) {
                      <td style="background:#fafafa;color:#bfbfbf">–</td>
                    } @else {
                      @let pair = overlap(d, a.manager.id, b.manager.id);
                      <td [style]="shareStyle(pair?.share ?? 0)">{{ (pair?.share ?? 0) * 100 | number: '1.0-0' }}%{{ (pair?.oppositeSides ?? 0) > 0 ? ' ⇄' : '' }}</td>
                    }
                  }
                </tr>
              }
            </table>
          </div>
        </nz-tab>
        <nz-tab nzTitle="Performance">
          <ng-template nz-tab>
            <app-performance-panel scope="Sleeve" [scopeId]="d.sleeve.id" />
          </ng-template>
        </nz-tab>
        <nz-tab nzTitle="Risk">
          <ng-template nz-tab>
            <app-risk-panel scope="Sleeve" [scopeId]="d.sleeve.id" />
          </ng-template>
        </nz-tab>
        <nz-tab nzTitle="Liquidity">
          <ng-template nz-tab>
            <app-liquidity-panel scope="Sleeve" [scopeId]="d.sleeve.id" />
          </ng-template>
        </nz-tab>
      </nz-tabs>
    }
  `,
})
export class SleevePage {
  private readonly api = inject(FundsApi);
  protected readonly page = pageData((params, filter) => this.api.sleeve(Number(params.get('sleeveId')), filter));
  protected readonly managerScale = computed(() => Math.max(1, ...(this.page.data()?.managers ?? []).flatMap((m: ManagerRow) => [m.totals.longUsd, -m.totals.shortUsd])));
  protected readonly mandate = computed(() => {
    const d = this.page.data();
    return d ? checks(d) : [];
  });
  private readonly matrixMax = computed(() => Math.max(1, ...(this.page.data()?.assets ?? []).flatMap((a) => a.holders.map((h) => Math.abs(h.netUsd)))));

  protected readonly abs = Math.abs;
  protected readonly whole = wholeUsd;

  protected firstWord(name: string): string {
    return name.split(' ')[0];
  }

  protected net(asset: AssetRow, managerId: number): number | null {
    return asset.holders.find((h) => h.managerId === managerId)?.netUsd ?? null;
  }

  protected heatStyle(value: number): Record<string, string> {
    return heat(value, this.matrixMax());
  }

  protected shareStyle(value: number): Record<string, string> {
    const t = Math.max(0, Math.min(1, value));
    const rgb = [232, 241, 252].map((c, i) => Math.round(c + ([16, 66, 129][i] - c) * t));
    return { background: `rgb(${rgb.join(',')})`, color: t > 0.5 ? '#fff' : '#1f1f1f' };
  }

  protected overlap(d: SleeveDetail, a: number, b: number) {
    return d.overlaps.find((o) => (o.managerA === a && o.managerB === b) || (o.managerA === b && o.managerB === a));
  }
}

/** The limits that can be checked from positions alone. */
function checks(d: SleeveDetail): MandateCheck[] {
  const nav = d.totals.navUsd;
  const value = (kind: string): number | undefined => d.limits.find((l: MandateLimit) => l.kind === kind)?.value;
  const result: MandateCheck[] = [];
  const gross = value('GrossExposureMax');
  if (gross !== undefined && nav > 0) {
    result.push({ rule: 'Gross exposure ÷ NAV', current: pct(d.totals.grossUsd / nav, 0, false), limit: '≤ ' + pct(gross, 0, false), ok: d.totals.grossUsd / nav <= gross });
  }

  const min = value('NetExposureMin');
  const max = value('NetExposureMax');
  if ((min !== undefined || max !== undefined) && nav > 0) {
    const net = d.totals.netUsd / nav;
    result.push({
      rule: 'Net exposure ÷ NAV',
      current: pct(net, 1),
      limit: `${min !== undefined ? pct(min, 0) : '−∞'} to ${max !== undefined ? pct(max, 0) : '+∞'}`,
      ok: (min === undefined || net >= min) && (max === undefined || net <= max),
    });
  }

  const single = value('SingleAssetMax');
  if (single !== undefined && nav > 0 && d.assets.length > 0) {
    const largest = d.assets[0];
    result.push({
      rule: `Largest single asset (${largest.asset.symbol})`,
      current: pct(largest.grossUsd / nav, 1, false),
      limit: '≤ ' + pct(single, 0, false),
      ok: largest.grossUsd / nav <= single,
    });
  }

  return result;
}
