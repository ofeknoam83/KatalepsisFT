import { DatePipe, DecimalPipe, NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { Store } from '@ngxs/store';
import { NzAlertModule } from 'ng-zorro-antd/alert';
import { NzSpinModule } from 'ng-zorro-antd/spin';
import { NzTableModule } from 'ng-zorro-antd/table';
import { catchError, combineLatest, of, switchMap, tap } from 'rxjs';
import { ViewState } from '../funds/view.state';
import { FORMAT_PIPES } from '../shared/format';
import { heat } from '../shared/heat';
import { RiaChartComponent, RiaChartSeries } from '../shared/ria-chart.component';
import { PerformanceScope, PnlLine } from './models';
import { PerformanceApi } from './performance.api';
import { PerformanceControls } from './performance-controls';

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

/** Returns against a benchmark for a fund, a sleeve or a manager. Used as a tab on each of those pages. */
@Component({
  selector: 'app-performance-panel',
  imports: [DatePipe, DecimalPipe, NgTemplateOutlet, RouterLink, NzAlertModule, NzSpinModule, NzTableModule, RiaChartComponent, PerformanceControls, ...FORMAT_PIPES],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (failed()) {
      <nz-alert nzType="error" nzMessage="Could not load the performance figures." nzShowIcon />
    } @else if (data() === null) {
      <nz-spin nzSimple style="display:block;margin:60px auto" />
    }
    @if (data(); as d) {
      <app-performance-controls [benchmarks]="d.benchmarks" [current]="d.benchmark" [range]="(d.from | date: 'd MMM y' : 'UTC') + ' – ' + (d.asOf | date: 'd MMM y' : 'UTC')" />

      <div class="tiles">
        <div class="tile"><div class="k">Return</div><div class="v" [class]="d.stats.totalReturn | tone">{{ d.stats.totalReturn | pct }}</div><div class="d">{{ d.benchmark.name }} {{ d.benchmarkStats.totalReturn | pct }}</div></div>
        <div class="tile"><div class="k">Excess over benchmark</div><div class="v" [class]="d.comparison.excessReturn | tone">{{ d.comparison.excessReturn | pct }}</div><div class="d">Information ratio {{ d.comparison.informationRatio === null ? '–' : (d.comparison.informationRatio | number: '1.2-2') }}</div></div>
        <div class="tile"><div class="k">Beta / correlation</div><div class="v">{{ d.comparison.beta === null ? '–' : (d.comparison.beta | number: '1.2-2') }} / {{ d.comparison.correlation === null ? '–' : (d.comparison.correlation | number: '1.2-2') }}</div><div class="d">to {{ d.benchmark.name }}</div></div>
        <div class="tile"><div class="k">Volatility</div><div class="v">{{ d.stats.volatility | pct: 1 : false }}</div><div class="d">Annualised · tracking error {{ d.comparison.trackingError | pct: 1 : false }}</div></div>
        <div class="tile"><div class="k">Sharpe</div><div class="v">{{ d.stats.sharpe === null ? '–' : (d.stats.sharpe | number: '1.2-2') }}</div><div class="d">Sortino {{ d.stats.sortino === null ? '–' : (d.stats.sortino | number: '1.2-2') }}</div></div>
        <div class="tile"><div class="k">Max drawdown</div><div class="v">{{ d.stats.maxDrawdown | pct: 1 : false }}</div><div class="d">{{ d.benchmark.name }} {{ d.benchmarkStats.maxDrawdown | pct: 1 : false }}</div></div>
      </div>

      <div class="cols" style="grid-template-columns:1.9fr 1fr">
        <div class="panel">
          <h3>{{ d.scope.name }} against {{ d.benchmark.name }} <span class="hint">Cumulative return, time-weighted</span></h3>
          <app-ria-chart [labels]="d.dates" [series]="lines()" yFormat="percent" [height]="270" [ariaLabel]="'Cumulative return of ' + d.scope.name + ' against ' + d.benchmark.name" testId="performance-chart" />
        </div>
        <div class="panel">
          <h3>Returns by period</h3>
          <nz-table [nzData]="d.periods" nzTemplateMode [nzShowPagination]="false" nzSize="small">
            <thead><tr><th>Period</th><th class="num">{{ d.scope.kind }}</th><th class="num">{{ d.benchmark.name }}</th><th class="num">Excess</th></tr></thead>
            <tbody>
              @for (row of d.periods; track row.label) {
                <tr>
                  <td>{{ row.label }}</td>
                  <td class="num" [class]="row.portfolio | tone">{{ row.portfolio | pct: 2 }}</td>
                  <td class="num" [class]="row.benchmark | tone">{{ row.benchmark | pct: 2 }}</td>
                  <td class="num" [class]="excess(row.portfolio, row.benchmark) | tone">{{ excess(row.portfolio, row.benchmark) | pct: 2 }}</td>
                </tr>
              }
            </tbody>
          </nz-table>
          <p style="color:#8c8c8c;font-size:12px;margin:8px 0 0">These rows always use their own window, whatever period is selected above.</p>
        </div>
      </div>

      <div class="panel">
        <h3>Drawdown <span class="hint">Below the running peak</span></h3>
        <app-ria-chart [labels]="d.dates" [series]="drawdownLines()" yFormat="percent" [height]="140" [ariaLabel]="'Drawdown of ' + d.scope.name" testId="drawdown-chart" />
      </div>

      @if (d.children.length > 0) {
        <div class="panel">
          <h3>{{ d.scope.kind === 'Fund' ? 'Sleeves' : 'Managers' }} against {{ d.benchmark.name }}</h3>
          <nz-table [nzData]="d.children" nzTemplateMode [nzShowPagination]="false" nzSize="small">
            <thead>
              <tr>
                <th>Name</th><th class="num">NAV</th><th class="num">Return</th><th class="num">Excess</th><th class="num">Beta</th><th class="num">Correlation</th>
                <th class="num">Tracking error</th><th class="num">Volatility</th><th class="num">Sharpe</th><th class="num">Max drawdown</th>
              </tr>
            </thead>
            <tbody>
              @for (row of d.children; track row.id) {
                <tr>
                  <td><a [routerLink]="[row.kind === 'Sleeve' ? '/sleeves' : '/managers', row.id]"><b>{{ row.name }}</b></a></td>
                  <td class="num">{{ row.navUsd | usd }}</td>
                  <td class="num" [class]="row.stats.totalReturn | tone">{{ row.stats.totalReturn | pct }}</td>
                  <td class="num" [class]="row.comparison.excessReturn | tone">{{ row.comparison.excessReturn | pct }}</td>
                  <td class="num">{{ row.comparison.beta === null ? '–' : (row.comparison.beta | number: '1.2-2') }}</td>
                  <td class="num">{{ row.comparison.correlation === null ? '–' : (row.comparison.correlation | number: '1.2-2') }}</td>
                  <td class="num">{{ row.comparison.trackingError | pct: 1 : false }}</td>
                  <td class="num">{{ row.stats.volatility | pct: 1 : false }}</td>
                  <td class="num">{{ row.stats.sharpe === null ? '–' : (row.stats.sharpe | number: '1.2-2') }}</td>
                  <td class="num">{{ row.stats.maxDrawdown | pct: 1 : false }}</td>
                </tr>
              }
              <tr class="total-row">
                <td>{{ d.scope.name }}</td><td class="num">{{ navTotal() | usd }}</td>
                <td class="num">{{ d.stats.totalReturn | pct }}</td><td class="num">{{ d.comparison.excessReturn | pct }}</td>
                <td class="num">{{ d.comparison.beta === null ? '–' : (d.comparison.beta | number: '1.2-2') }}</td>
                <td class="num">{{ d.comparison.correlation === null ? '–' : (d.comparison.correlation | number: '1.2-2') }}</td>
                <td class="num">{{ d.comparison.trackingError | pct: 1 : false }}</td><td class="num">{{ d.stats.volatility | pct: 1 : false }}</td>
                <td class="num">{{ d.stats.sharpe === null ? '–' : (d.stats.sharpe | number: '1.2-2') }}</td><td class="num">{{ d.stats.maxDrawdown | pct: 1 : false }}</td>
              </tr>
            </tbody>
          </nz-table>
        </div>
      }

      <div class="panel">
        <h3>
          Where the profit and loss came from
          <span class="hint">{{ d.attribution.explainedUsd | usd: 2 }} from price moves on positions held · NAV changed by {{ d.attribution.navChangeUsd | usd: 2 }}</span>
        </h3>
        <div style="display:grid;grid-template-columns:repeat(auto-fit,minmax(260px,1fr));gap:24px;align-items:start">
          @if (d.attribution.byChild.length > 0) {
            <div>
              <div class="sub-head">By {{ d.scope.kind === 'Fund' ? 'sleeve' : 'manager' }}</div>
              <ng-container *ngTemplateOutlet="pnl; context: { rows: d.attribution.byChild, start: d.attribution.startNavUsd }" />
            </div>
          }
          <div>
            <div class="sub-head">By instrument type</div>
            <ng-container *ngTemplateOutlet="pnl; context: { rows: d.attribution.byType, start: d.attribution.startNavUsd }" />
          </div>
          <div>
            <div class="sub-head">By asset</div>
            <ng-container *ngTemplateOutlet="pnl; context: { rows: d.attribution.byAsset, start: d.attribution.startNavUsd }" />
          </div>
        </div>
        <p style="color:#8c8c8c;font-size:12px;margin:10px 0 0">Each position earns the quantity held at the previous close times the day's price move. Points are the profit as a share of NAV at the start of the period.</p>
      </div>

      <div class="panel" style="overflow-x:auto">
        <h3>Monthly returns</h3>
        <table class="matrix">
          <tr>
            <th></th>
            @for (month of d.monthly; track month.month) {
              <th>{{ monthLabel(month.month) }}</th>
            }
          </tr>
          <tr>
            <th class="row">{{ d.scope.name }}</th>
            @for (month of d.monthly; track month.month) {
              <td [style]="heatStyle(month.portfolio)">{{ month.portfolio | pct }}</td>
            }
          </tr>
          <tr>
            <th class="row">{{ d.benchmark.name }}</th>
            @for (month of d.monthly; track month.month) {
              <td [style]="heatStyle(month.benchmark)">{{ month.benchmark | pct }}</td>
            }
          </tr>
          <tr>
            <th class="row">Excess</th>
            @for (month of d.monthly; track month.month) {
              <td [class]="month.portfolio - month.benchmark | tone">{{ month.portfolio - month.benchmark | pct }}</td>
            }
          </tr>
        </table>
      </div>
    }

    <ng-template #pnl let-rows="rows" let-start="start">
      <table style="width:100%;font-size:12.5px;border-collapse:collapse">
        @for (row of rows; track row.key) {
          <tr style="border-bottom:1px solid #f0f0f0">
            <td style="padding:5px 0">{{ row.key }}</td>
            <td class="num" [class]="row.pnlUsd | tone" style="padding:5px 0 5px 12px">{{ row.pnlUsd | usd: 2 }}</td>
            <td class="num" style="padding:5px 0 5px 12px;color:#8c8c8c;width:70px">{{ points(row, start) }}</td>
          </tr>
        }
      </table>
    </ng-template>
  `,
  styles: `
    .sub-head {
      font-size: 12px;
      color: #8c8c8c;
      margin-bottom: 4px;
    }
  `,
})
export class PerformancePanel {
  private readonly api = inject(PerformanceApi);
  private readonly store = inject(Store);

  readonly scope = input.required<PerformanceScope>();
  readonly scopeId = input.required<number>();

  protected readonly failed = signal(false);
  protected readonly data = toSignal(
    combineLatest([toObservable(this.scope), toObservable(this.scopeId), this.store.select(ViewState.benchmarkId), this.store.select(ViewState.period)]).pipe(
      switchMap(([scope, id, benchmarkId, period]) =>
        this.api.get(scope, id, benchmarkId, period).pipe(
          tap(() => this.failed.set(false)),
          catchError(() => {
            this.failed.set(true);
            return of(null);
          }),
        ),
      ),
    ),
    { initialValue: null },
  );

  protected readonly lines = computed<RiaChartSeries[]>(() => {
    const d = this.data();
    return d
      ? [
          { label: d.scope.name, data: d.portfolio, color: '#0d366b', width: 2.2 },
          { label: d.benchmark.name, data: d.benchmarkSeries, color: '#898781', dash: [5, 4] },
        ]
      : [];
  });
  protected readonly drawdownLines = computed<RiaChartSeries[]>(() => {
    const d = this.data();
    return d ? [{ label: 'Drawdown', data: d.drawdown, color: '#d03b3b', fill: true }] : [];
  });
  protected readonly navTotal = computed(() => (this.data()?.children ?? []).reduce((sum, c) => sum + c.navUsd, 0));
  private readonly monthMax = computed(() => Math.max(0.05, ...(this.data()?.monthly ?? []).flatMap((m) => [Math.abs(m.portfolio), Math.abs(m.benchmark)])));

  protected excess(portfolio: number | null, benchmark: number | null): number | null {
    return portfolio === null || benchmark === null ? null : portfolio - benchmark;
  }

  protected heatStyle(value: number): Record<string, string> {
    return heat(value, this.monthMax());
  }

  protected monthLabel(month: string): string {
    const [year, number] = month.split('-');
    return `${MONTHS[Number(number) - 1]} ${year.slice(2)}`;
  }

  protected points(row: PnlLine, start: number): string {
    if (start <= 0) {
      return '';
    }

    const value = (row.pnlUsd / start) * 100;
    return `${value > 0 ? '+' : value < 0 ? '−' : ''}${Math.abs(value).toFixed(1)} pts`;
  }
}
