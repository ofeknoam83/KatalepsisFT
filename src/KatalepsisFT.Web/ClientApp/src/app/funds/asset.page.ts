import { DatePipe, DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import { Store } from '@ngxs/store';
import { catchError, combineLatest, of, switchMap } from 'rxjs';
import { PerformanceApi } from '../performance/performance.api';
import { PerformanceControls } from '../performance/performance-controls';
import { RiaChartComponent, RiaChartSeries } from '../shared/ria-chart.component';
import { ViewState } from './view.state';
import { RouterLink } from '@angular/router';
import { NzAlertModule } from 'ng-zorro-antd/alert';
import { NzTableModule } from 'ng-zorro-antd/table';
import { NzTagModule } from 'ng-zorro-antd/tag';
import { ExposureBar } from '../shared/exposure-bar';
import { FORMAT_PIPES } from '../shared/format';
import { pageData } from '../shared/page-data';
import { PageHead } from '../shared/page-head';
import { FundsApi } from './funds.api';
import { SLEEVE_COLORS } from './models';

@Component({
  selector: 'app-asset-page',
  imports: [DatePipe, DecimalPipe, RouterLink, PerformanceControls, RiaChartComponent, NzAlertModule, NzTableModule, NzTagModule, ExposureBar, PageHead, ...FORMAT_PIPES],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (page.failed()) {
      <nz-alert nzType="error" nzMessage="Could not load the asset." nzShowIcon />
    }
    @if (page.data(); as d) {
      <app-page-head
        [crumbs]="[{ label: d.fund.name, link: ['/funds', d.fund.id] }, { label: 'Holdings', link: ['/funds', d.fund.id, 'holdings'] }, { label: d.asset.symbol }]"
        [title]="d.asset.symbol"
        [demo]="d.fund.isDemo"
        [sub]="d.asset.name + ' · held in ' + sleevesHolding() + ' of ' + d.sleeves.length + ' sleeves by ' + d.managers + ' managers'"
        [asOf]="d.asOf"
        [showFilter]="false"
      />

      <div class="tiles">
        <div class="tile"><div class="k">Price</div><div class="v">{{ d.asset.price | price }}</div><div class="d">1D <span [class]="d.asset.change1D | tone">{{ d.asset.change1D | pct }}</span></div></div>
        <div class="tile"><div class="k">Return YTD</div><div class="v" [class]="d.asset.changeYtd | tone">{{ d.asset.changeYtd | pct }}</div><div class="d">30D <span [class]="d.asset.change30D | tone">{{ d.asset.change30D | pct }}</span></div></div>
        <div class="tile"><div class="k">Fund exposure, gross</div><div class="v">{{ d.grossUsd | compact }}</div><div class="x">{{ d.grossUsd | usd }}</div><div class="d">Net <span [class]="d.netUsd | tone">{{ d.netUsd | usd }}</span> · long {{ d.longUsd | usd }} · short {{ d.shortUsd | usd }}</div></div>
        <div class="tile"><div class="k">Share of fund</div><div class="v">{{ d.grossUsd | share: d.fundNavUsd }}</div><div class="d">Gross ÷ fund NAV</div></div>
        <div class="tile"><div class="k">Average daily volume</div><div class="v">{{ d.asset.averageDailyVolumeUsd | compact }}</div><div class="x">{{ d.asset.averageDailyVolumeUsd | usd }}</div><div class="d">30 days, Binance spot · position is {{ volumeMultiple() }}</div></div>
      </div>

      <div class="panel">
        <h3>Where {{ d.asset.symbol }} sits <span class="hint">Sleeve → manager → instrument · click a row to drill back down</span></h3>
        <nz-table [nzData]="d.sleeves" nzTemplateMode [nzShowPagination]="false" nzSize="small">
          <thead>
            <tr>
              <th>Sleeve / manager</th><th>Instrument</th><th>Type</th><th>Side</th><th class="num">Quantity</th><th class="num">Exposure</th>
              <th class="num">% of manager</th><th class="num">% of sleeve</th><th class="num">% of fund</th>
            </tr>
          </thead>
          <tbody>
            @for (sleeve of d.sleeves; track sleeve.sleeve.id; let i = $index) {
              <tr class="group-row">
                <td><span class="swatch" [style.background]="colors[i % colors.length]"></span><a [routerLink]="['/sleeves', sleeve.sleeve.id]">{{ sleeve.sleeve.name }}</a></td>
                @if (sleeve.positions.length === 0) {
                  <td colspan="8" style="font-weight:400;color:var(--kft-text-3)">Not held</td>
                } @else {
                  <td></td><td></td><td></td><td></td>
                  <td class="num" [class]="sleeve.longUsd + sleeve.shortUsd | tone">{{ sleeve.longUsd + sleeve.shortUsd | usd }} net</td>
                  <td></td>
                  <td class="num">{{ sleeve.longUsd - sleeve.shortUsd | share: sleeve.sleeveNavUsd }}</td>
                  <td class="num">{{ sleeve.longUsd - sleeve.shortUsd | share: d.fundNavUsd }}</td>
                }
              </tr>
              @for (position of sleeve.positions; track position.manager.id + position.instrument) {
                <tr>
                  <td style="padding-left:30px"><a [routerLink]="['/managers', position.manager.id]">{{ position.manager.name }}</a></td>
                  <td>{{ position.instrument }}</td>
                  <td><nz-tag>{{ position.type }}</nz-tag></td>
                  <td><nz-tag [nzColor]="position.exposureUsd >= 0 ? 'green' : 'geekblue'">{{ position.exposureUsd >= 0 ? 'Long' : 'Short' }}</nz-tag></td>
                  <td class="num">{{ abs(position.quantity) | number: (abs(position.quantity) >= 1000 ? '1.0-0' : '1.0-4') }}</td>
                  <td class="num" [class]="position.exposureUsd | tone">{{ position.exposureUsd | usd: 2 }}</td>
                  <td class="num">{{ abs(position.exposureUsd) | share: position.managerNavUsd }}</td>
                  <td class="num">{{ abs(position.exposureUsd) | share: sleeve.sleeveNavUsd }}</td>
                  <td class="num">{{ abs(position.exposureUsd) | share: d.fundNavUsd }}</td>
                </tr>
              }
            }
            <tr class="total-row">
              <td>Fund total</td><td></td><td></td><td></td><td></td>
              <td class="num">{{ d.netUsd | usd }} net</td><td></td><td></td><td class="num">{{ d.grossUsd | share: d.fundNavUsd }}</td>
            </tr>
          </tbody>
        </nz-table>
      </div>

      @if (performance(); as p) {
        <div class="panel">
          <h3>{{ d.asset.symbol }} against {{ p.benchmark.name }} <span class="hint">Cumulative price return · excess {{ p.comparison.excessReturn | pct }} · beta {{ p.comparison.beta === null ? '–' : (p.comparison.beta | number: '1.2-2') }}</span></h3>
          <app-performance-controls [benchmarks]="p.benchmarks" [current]="p.benchmark" [range]="(p.from | date: 'd MMM y' : 'UTC') + ' – ' + (p.asOf | date: 'd MMM y' : 'UTC')" />
          <app-ria-chart [labels]="p.dates" [series]="lines()" yFormat="percent" [height]="240" [ariaLabel]="'Price return of ' + d.asset.symbol + ' against ' + p.benchmark.name" testId="asset-chart" />
        </div>
      }

      <div class="cols">
        <div class="panel">
          <h3>By sleeve</h3>
          <nz-table [nzData]="d.sleeves" nzTemplateMode [nzShowPagination]="false" nzSize="small">
            <thead><tr><th>Sleeve</th><th class="num">Long</th><th class="num">Short</th><th style="width:45%">Short · Long</th></tr></thead>
            <tbody>
              @for (sleeve of d.sleeves; track sleeve.sleeve.id) {
                <tr>
                  <td>{{ sleeve.sleeve.name }}</td><td class="num">{{ sleeve.longUsd | usd }}</td><td class="num">{{ sleeve.shortUsd | usd }}</td>
                  <td><app-exposure-bar [long]="sleeve.longUsd" [short]="sleeve.shortUsd" [scale]="scale()" /></td>
                </tr>
              }
            </tbody>
          </nz-table>
        </div>
        <div class="panel">
          <h3>By instrument type</h3>
          <nz-table [nzData]="d.byType" nzTemplateMode [nzShowPagination]="false" nzSize="small">
            <thead><tr><th>Type</th><th class="num">Long</th><th class="num">Short</th><th class="num">Net</th></tr></thead>
            <tbody>
              @for (row of d.byType; track row.type) {
                <tr><td>{{ row.type }}</td><td class="num">{{ row.longUsd | usd }}</td><td class="num">{{ row.shortUsd | usd }}</td><td class="num" [class]="row.netUsd | tone">{{ row.netUsd | usd }}</td></tr>
              }
            </tbody>
          </nz-table>
        </div>
      </div>
    }
  `,
})
export class AssetPage {
  private readonly api = inject(FundsApi);

  protected readonly colors = SLEEVE_COLORS;
  protected readonly page = pageData((params) => this.api.asset(Number(params.get('fundId')), params.get('symbol') ?? ''));
  protected readonly sleevesHolding = computed(() => (this.page.data()?.sleeves ?? []).filter((s) => s.positions.length > 0).length);
  protected readonly scale = computed(() => Math.max(1, ...(this.page.data()?.sleeves ?? []).flatMap((s) => [s.longUsd, -s.shortUsd])));
  protected readonly volumeMultiple = computed(() => {
    const d = this.page.data();
    const volume = d?.asset.averageDailyVolumeUsd;
    return d && volume ? `${(d.grossUsd / volume).toFixed(2)}× a day's volume` : 'not comparable';
  });
  protected readonly abs = Math.abs;

  private readonly store = inject(Store);
  private readonly performanceApi = inject(PerformanceApi);
  protected readonly performance = toSignal(
    combineLatest([inject(ActivatedRoute).paramMap, this.store.select(ViewState.benchmarkId), this.store.select(ViewState.period)]).pipe(
      // An asset without price history simply has no chart.
      switchMap(([params, benchmarkId, period]) => this.performanceApi.asset(params.get('symbol') ?? '', benchmarkId, period).pipe(catchError(() => of(null)))),
    ),
    { initialValue: null },
  );
  protected readonly lines = computed<RiaChartSeries[]>(() => {
    const p = this.performance();
    return p
      ? [
          { label: p.symbol, data: p.asset, color: '#4338ca', width: 2.2 },
          { label: p.benchmark.name, data: p.benchmarkSeries, color: '#98a2b3', dash: [5, 4] },
        ]
      : [];
  });
}
