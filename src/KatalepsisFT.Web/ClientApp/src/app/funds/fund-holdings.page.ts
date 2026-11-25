import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { Coin } from '../shared/coin';
import { RouterLink } from '@angular/router';
import { NzAlertModule } from 'ng-zorro-antd/alert';
import { NzTableModule } from 'ng-zorro-antd/table';
import { NzTagModule } from 'ng-zorro-antd/tag';
import { FORMAT_PIPES } from '../shared/format';
import { pageData } from '../shared/page-data';
import { PageHead } from '../shared/page-head';
import { FundTabs } from './fund-tabs';
import { FundsApi } from './funds.api';
import { InstrumentType, SLEEVE_COLORS } from './models';

@Component({
  selector: 'app-fund-holdings-page',
  imports: [Coin, RouterLink, NzAlertModule, NzTableModule, NzTagModule, PageHead, FundTabs, ...FORMAT_PIPES],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (page.failed()) {
      <nz-alert nzType="error" nzMessage="Could not load the holdings." nzShowIcon />
    }
    @if (page.data(); as d) {
      <app-page-head [crumbs]="[{ label: d.fund.name, link: ['/funds', d.fund.id] }, { label: 'Holdings' }]" [title]="d.fund.name" [renameUrl]="'/api/funds/' + d.fund.id + '/name'" renameWhat="the fund" [demo]="d.fund.isDemo" sub="Holdings by asset" [asOf]="d.asOf" />
      <app-fund-tabs [fundId]="d.fund.id" />

      <div class="tiles">
        <div class="tile"><div class="k">Assets held</div><div class="v">{{ d.assets.length }}</div></div>
        <div class="tile"><div class="k">Long</div><div class="v">{{ d.totals.longUsd | compact }}</div><div class="x">{{ d.totals.longUsd | usd }}</div><div class="d">{{ d.totals.longUsd | share: d.totals.navUsd : 0 }} of NAV</div></div>
        <div class="tile"><div class="k">Short</div><div class="v">{{ d.totals.shortUsd | compact }}</div><div class="x">{{ d.totals.shortUsd | usd }}</div><div class="d">{{ -d.totals.shortUsd | share: d.totals.navUsd : 0 }} of NAV</div></div>
        <div class="tile"><div class="k">Gross</div><div class="v">{{ d.totals.grossUsd | compact }}</div><div class="x">{{ d.totals.grossUsd | usd }}</div><div class="d">{{ d.totals.grossUsd | share: d.totals.navUsd : 0 }} of NAV</div></div>
        <div class="tile"><div class="k">Net</div><div class="v">{{ d.totals.netUsd | compact }}</div><div class="x">{{ d.totals.netUsd | usd }}</div><div class="d">{{ d.totals.netUsd / d.totals.navUsd | pct: 0 : false }} of NAV</div></div>
        <div class="tile"><div class="k">Cash</div><div class="v">{{ d.totals.cashUsd | compact }}</div><div class="x">{{ d.totals.cashUsd | usd }}</div><div class="d">Not counted as exposure</div></div>
      </div>

      <div class="panel">
        <h3>Holdings by asset <span class="hint">Exposure is delta-adjusted · hover over an amount for the exact figure · click an asset to see where it sits across sleeves</span></h3>
        <nz-table #assets [nzData]="d.assets" [nzShowPagination]="false" [nzFrontPagination]="false" nzSize="small" [nzScroll]="{ x: '1240px' }">
          <thead>
            <tr>
              <th nzWidth="260px">Asset</th><th class="num">Price</th><th class="num">1D</th><th class="num">30D</th><th class="num">YTD</th>
              @for (type of types(); track type) {
                <th class="num">{{ type }}</th>
              }
              <th class="num">Long</th><th class="num">Short</th><th class="num">Gross</th><th class="num">Net</th><th class="num">% NAV</th><th class="num">Managers</th><th>Sleeves</th>
            </tr>
          </thead>
          <tbody>
            @for (row of assets.data; track row.asset.symbol) {
              <tr>
                <td class="nowrap">
                  <app-coin [symbol]="row.asset.symbol" [type]="row.netByType['Equity'] !== undefined ? 'Equity' : null" /><a [routerLink]="['/funds', d.fund.id, 'assets', row.asset.symbol]"><b>{{ row.asset.symbol }}</b></a>
                  @if (row.hasOffsettingManagers) {
                    <nz-tag nzColor="gold" style="margin-left:6px">Both sides</nz-tag>
                  }
                  @if (!d.fund.isDemo) {
                    <span data-testid="trade-links">
                      @if (row.netByType['Spot'] !== undefined) {
                        <a class="trade" [routerLink]="['/funds', d.fund.id, 'trading']" [queryParams]="{ instrument: 'Spot:' + row.asset.symbol }" title="Open the order ticket for this asset's spot market">{{ row.netByType['Perp'] !== undefined ? 'Trade spot' : 'Trade' }}</a>
                      }
                      @if (row.netByType['Perp'] !== undefined) {
                        <a class="trade" [routerLink]="['/funds', d.fund.id, 'trading']" [queryParams]="{ instrument: 'Perp:' + row.asset.symbol }" title="Open the order ticket for this asset's perpetual">{{ row.netByType['Spot'] !== undefined ? 'perp' : 'Trade' }}</a>
                      }
                    </span>
                  }
                </td>
                <td class="num">{{ row.asset.price | price }}</td>
                <td class="num"><span class="chip" [class]="row.asset.change1D | tone">{{ row.asset.change1D | pct }}</span></td>
                <td class="num"><span class="chip" [class]="row.asset.change30D | tone">{{ row.asset.change30D | pct }}</span></td>
                <td class="num"><span class="chip" [class]="row.asset.changeYtd | tone">{{ row.asset.changeYtd | pct }}</span></td>
                @for (type of types(); track type) {
                  <td class="num" [class]="row.netByType[type] | tone" [title]="row.netByType[type] === undefined ? '' : (row.netByType[type] | usd)">{{ row.netByType[type] === undefined ? '–' : (row.netByType[type] | compact) }}</td>
                }
                <td class="num" [title]="row.longUsd | usd">{{ row.longUsd | compact }}</td>
                <td class="num" [title]="row.shortUsd | usd">{{ row.shortUsd | compact }}</td>
                <td class="num" [title]="row.grossUsd | usd">{{ row.grossUsd | compact }}</td>
                <td class="num" [class]="row.netUsd | tone" [title]="row.netUsd | usd">{{ row.netUsd | compact }}</td>
                <td class="num">{{ row.grossUsd | share: d.totals.navUsd }}</td>
                <td class="num">{{ row.holders.length }}</td>
                <td>
                  @for (sleeve of d.sleeves; track sleeve.id; let i = $index) {
                    @if (row.sleeveIds.includes(sleeve.id)) {
                      <span class="swatch" [style.background]="colors[i % colors.length]" [title]="sleeve.name"></span>
                    }
                  }
                </td>
              </tr>
            }
          </tbody>
        </nz-table>
        <div style="margin-top:10px;font-size:12px;color:var(--kft-text-2)">
          @for (sleeve of d.sleeves; track sleeve.id; let i = $index) {
            <span style="margin-right:16px"><span class="swatch" [style.background]="colors[i % colors.length]"></span>{{ sleeve.name }}</span>
          }
        </div>
      </div>
    }
  `,
})
export class FundHoldingsPage {
  private readonly api = inject(FundsApi);

  protected readonly colors = SLEEVE_COLORS;
  protected readonly page = pageData((params, filter) => this.api.holdings(Number(params.get('fundId')), filter));
  /** Only the instrument types that are actually held get a column. */
  protected readonly types = computed<InstrumentType[]>(() => {
    const held = new Set((this.page.data()?.assets ?? []).flatMap((a) => Object.keys(a.netByType)));
    return (['Spot', 'Perp', 'Option', 'Future', 'Equity'] as InstrumentType[]).filter((t) => held.has(t));
  });
}
