import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { NzAlertModule } from 'ng-zorro-antd/alert';
import { NzTableModule } from 'ng-zorro-antd/table';
import { ExposureBar } from '../shared/exposure-bar';
import { FORMAT_PIPES } from '../shared/format';
import { pageData } from '../shared/page-data';
import { AlertStrip } from '../alerts/alert-strip';
import { PageHead } from '../shared/page-head';
import { FundTabs } from './fund-tabs';
import { FundsApi } from './funds.api';
import { IntradayPanel } from './intraday-panel';
import { SLEEVE_COLORS } from './models';

@Component({
  selector: 'app-fund-overview-page',
  imports: [RouterLink, NzAlertModule, NzTableModule, ExposureBar, PageHead, FundTabs, ...FORMAT_PIPES, AlertStrip, IntradayPanel],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (page.failed()) {
      <nz-alert nzType="error" nzMessage="Could not load the fund." nzShowIcon />
    }
    @if (page.data(); as d) {
      <app-page-head
        [crumbs]="[{ label: d.fund.name }]"
        [title]="d.fund.name"
        [demo]="d.fund.isDemo"
        [sub]="d.sleeves.length + ' sleeves · ' + managerCount() + ' managers'"
        [asOf]="d.asOf"
      />
      <app-fund-tabs [fundId]="d.fund.id" />
      <app-alert-strip [fundId]="d.fund.id" />

      <div class="tiles">
        <div class="tile"><div class="k">Net asset value</div><div class="v">{{ d.totals.navUsd | usd }}</div><div class="d">Cash {{ d.totals.cashUsd | usd }}</div></div>
        <div class="tile"><div class="k">Long exposure</div><div class="v">{{ d.totals.longUsd | usd }}</div><div class="d">{{ d.totals.longUsd | share: d.totals.navUsd : 0 }} of NAV</div></div>
        <div class="tile"><div class="k">Short exposure</div><div class="v">{{ d.totals.shortUsd | usd }}</div><div class="d">{{ -d.totals.shortUsd | share: d.totals.navUsd : 0 }} of NAV</div></div>
        <div class="tile"><div class="k">Gross exposure</div><div class="v">{{ d.totals.grossUsd | share: d.totals.navUsd : 0 }}</div><div class="d">{{ d.totals.grossUsd | usd }}</div></div>
        <div class="tile"><div class="k">Net exposure</div><div class="v">{{ d.totals.netUsd / d.totals.navUsd | pct: 0 : false }}</div><div class="d">{{ d.totals.netUsd | usd }}</div></div>
      </div>

      <app-intraday-panel [fundId]="d.fund.id" />

      <div class="panel">
        <h3>Sleeves <span class="hint">Click a sleeve to drill down</span></h3>
        <nz-table #sleeves [nzData]="d.sleeves" [nzShowPagination]="false" [nzFrontPagination]="false" nzSize="small">
          <thead>
            <tr>
              <th>Sleeve</th><th>Mandate</th><th class="num">Managers</th><th class="num">NAV</th><th class="num">% of fund</th><th class="num">Long</th>
              <th class="num">Short</th><th class="num">Gross % NAV</th><th class="num">Net % NAV</th><th style="width:22%">Short · Long</th>
            </tr>
          </thead>
          <tbody>
            @for (row of sleeves.data; track row.sleeve.id; let i = $index) {
              <tr>
                <td><span class="swatch" [style.background]="colors[i % colors.length]"></span><a [routerLink]="['/sleeves', row.sleeve.id]"><b>{{ row.sleeve.name }}</b></a></td>
                <td>{{ row.sleeve.mandateType | label }}</td>
                <td class="num">{{ row.managers }}</td>
                <td class="num">{{ row.totals.navUsd | usd }}</td>
                <td class="num">{{ row.totals.navUsd | share: d.totals.navUsd : 0 }}</td>
                <td class="num">{{ row.totals.longUsd | usd }}</td>
                <td class="num">{{ row.totals.shortUsd | usd }}</td>
                <td class="num">{{ row.totals.grossUsd | share: row.totals.navUsd : 0 }}</td>
                <td class="num">{{ row.totals.netUsd / row.totals.navUsd | pct: 0 : false }}</td>
                <td><app-exposure-bar [long]="row.totals.longUsd" [short]="row.totals.shortUsd" [scale]="sleeveScale()" /></td>
              </tr>
            }
            <tr class="total-row">
              <td>Fund</td><td></td><td class="num">{{ managerCount() }}</td><td class="num">{{ d.totals.navUsd | usd }}</td><td class="num">100%</td>
              <td class="num">{{ d.totals.longUsd | usd }}</td><td class="num">{{ d.totals.shortUsd | usd }}</td>
              <td class="num">{{ d.totals.grossUsd | share: d.totals.navUsd : 0 }}</td><td class="num">{{ d.totals.netUsd / d.totals.navUsd | pct: 0 : false }}</td><td></td>
            </tr>
          </tbody>
        </nz-table>
      </div>

      <div class="cols">
        <div class="panel">
          <h3>Top assets by gross exposure <span class="hint">Delta-adjusted · <a [routerLink]="['/funds', d.fund.id, 'holdings']">all holdings</a></span></h3>
          <nz-table #assets [nzData]="d.topAssets" [nzShowPagination]="false" [nzFrontPagination]="false" nzSize="small">
            <thead>
              <tr><th>Asset</th><th class="num">Long</th><th class="num">Short</th><th class="num">Net</th><th class="num">Managers</th><th style="width:34%">Short · Long</th></tr>
            </thead>
            <tbody>
              @for (row of assets.data; track row.asset.symbol) {
                <tr>
                  <td><span class="coin">{{ row.asset.symbol }}</span><a [routerLink]="['/funds', d.fund.id, 'assets', row.asset.symbol]"><b>{{ row.asset.symbol }}</b></a></td>
                  <td class="num">{{ row.longUsd | usd }}</td>
                  <td class="num">{{ row.shortUsd | usd }}</td>
                  <td class="num" [class]="row.netUsd | tone">{{ row.netUsd | usd }}</td>
                  <td class="num">{{ row.holders.length }}</td>
                  <td><app-exposure-bar [long]="row.longUsd" [short]="row.shortUsd" [scale]="assetScale()" /></td>
                </tr>
              }
            </tbody>
          </nz-table>
        </div>
        <div class="panel">
          <h3>By instrument type <span class="hint">Whole fund, not filtered</span></h3>
          <nz-table #types [nzData]="d.byType" [nzShowPagination]="false" [nzFrontPagination]="false" nzSize="small">
            <thead>
              <tr><th>Type</th><th class="num">Long</th><th class="num">Short</th><th class="num">Gross</th><th class="num">Net</th></tr>
            </thead>
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
    }
  `,
})
export class FundOverviewPage {
  private readonly api = inject(FundsApi);

  protected readonly colors = SLEEVE_COLORS;
  protected readonly page = pageData((params, filter) => this.api.overview(Number(params.get('fundId')), filter));
  protected readonly managerCount = computed(() => (this.page.data()?.sleeves ?? []).reduce((sum, s) => sum + s.managers, 0));
  protected readonly sleeveScale = computed(() => Math.max(1, ...(this.page.data()?.sleeves ?? []).flatMap((s) => [s.totals.longUsd, -s.totals.shortUsd])));
  protected readonly assetScale = computed(() => Math.max(1, ...(this.page.data()?.topAssets ?? []).flatMap((a) => [a.longUsd, -a.shortUsd])));
}
