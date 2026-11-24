import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { toObservable } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { NzAlertModule } from 'ng-zorro-antd/alert';
import { NzSpinModule } from 'ng-zorro-antd/spin';
import { NzTableModule } from 'ng-zorro-antd/table';
import { NzTagModule } from 'ng-zorro-antd/tag';
import { LiveService } from '../core/live.service';
import { combineLatest } from 'rxjs';
import { PerformanceScope } from '../performance/models';
import { FORMAT_PIPES, pct } from '../shared/format';
import { heat } from '../shared/heat';
import { keptData } from '../shared/page-data';
import { RiaChartComponent, RiaChartSeries } from '../shared/ria-chart.component';
import { MandateCheck } from './models';
import { RiskApi } from './risk.api';

const CHILD_COLORS = ['#2a78d6', '#e08a00', '#1f9d74', '#8a4fd3', '#c2417a', '#5b6b7a'];

/** Scenario matrix, value at risk, mandate checks, volatility and correlation for a fund, a sleeve or a manager. */
@Component({
  selector: 'app-risk-panel',
  imports: [DecimalPipe, RouterLink, NzAlertModule, NzSpinModule, NzTableModule, NzTagModule, RiaChartComponent, ...FORMAT_PIPES],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (failed()) {
      <nz-alert nzType="error" nzMessage="Could not load the risk figures." nzShowIcon />
    } @else if (missing()) {
      <nz-alert nzType="info" nzMessage="Nothing to show here yet. Risk figures need positions to work from." nzShowIcon data-testid="nothing-yet" />
    } @else if (data() === null) {
      <nz-spin nzSimple style="display:block;margin:60px auto" />
    }
    @if (data(); as d) {
      <div class="tiles">
        <div class="tile"><div class="k">1-day value at risk, 95%</div><div class="v">{{ d.var.valueAtRiskUsd | compact }}</div><div class="x">{{ d.var.valueAtRiskUsd | usd }}</div><div class="d">{{ d.var.valueAtRiskUsd | share: d.totals.navUsd : 2 }} of NAV · today's book over {{ d.var.days }} days</div>
          @if (d.var.uncoveredUsd > 0) {
            <div class="d" style="color:var(--kft-warn)" data-testid="var-uncovered">Leaves out {{ d.var.uncoveredUsd | usd }} in {{ d.var.uncoveredAssets.slice(0, 4).join(', ') }}{{ d.var.uncoveredAssets.length > 4 ? ' and ' + (d.var.uncoveredAssets.length - 4) + ' more' : '' }}: no price history to work from.</div>
          }
        </div>
        <div class="tile"><div class="k">Expected shortfall, 95%</div><div class="v">{{ d.var.expectedShortfallUsd | compact }}</div><div class="x">{{ d.var.expectedShortfallUsd | usd }}</div><div class="d">Average loss on the worst 5% of days</div></div>
        <div class="tile"><div class="k">Gross leverage</div><div class="v">{{ leverage(d.totals.grossUsd, d.totals.navUsd) }}</div><div class="d">Gross {{ d.totals.grossUsd | usd }} on NAV {{ d.totals.navUsd | usd }}</div></div>
        <div class="tile"><div class="k">Net exposure</div><div class="v">{{ d.totals.netUsd | share: d.totals.navUsd }}</div><div class="d">{{ d.totals.netUsd | usd }} net</div></div>
        <div class="tile"><div class="k">Top 5 assets</div><div class="v">{{ d.topFiveShareOfGross | pct: 1 : false }}</div><div class="d">of gross exposure</div></div>
        <div class="tile"><div class="k">Mandate</div><div class="v" [class]="breaches() > 0 ? 'down' : 'up'">{{ breaches() === 0 ? 'Within limits' : breaches() + (breaches() === 1 ? ' breach' : ' breaches') }}</div><div class="d">{{ d.mandate.length }} limits checked</div></div>
      </div>

      <div class="cols" style="grid-template-columns:1fr 1fr">
        <div class="panel" data-testid="scenario">
          <h3>Scenario matrix <span class="hint">Profit or loss if {{ d.scenario.referenceAsset }} moves and every other asset follows by its beta</span></h3>
          <table class="matrix">
            <tr>
              <th class="row">{{ d.scenario.referenceAsset }} move</th>
              @for (m of d.scenario.betaMultipliers; track m) {
                <th>{{ m === 1 ? 'Betas as measured' : 'Betas × ' + m }}</th>
              }
            </tr>
            @for (shock of d.scenario.shocks; track shock; let i = $index) {
              <tr>
                <th class="row">{{ shock | pct: 0 }}</th>
                @for (cell of d.scenario.cells[i]; track $index) {
                  <td [style]="heatStyle(cell)">{{ cell | usd: 2 }}</td>
                }
              </tr>
            }
          </table>
          <p class="note">Betas are measured against {{ d.scenario.referenceAsset }} on the last 180 days of daily returns. The outer columns ask what happens if the other assets react half as much, or half as much again.</p>
        </div>

        <div class="panel" data-testid="mandate">
          <h3>Mandate limits</h3>
          <nz-table [nzData]="d.mandate" nzTemplateMode [nzShowPagination]="false" nzSize="small">
            <thead><tr>@if (d.scope.kind === 'Fund') {<th>Sleeve</th>}<th>Rule</th><th class="num">Now</th><th class="num">Limit</th><th></th></tr></thead>
            <tbody>
              @for (check of d.mandate; track check.sleeveId + check.kind) {
                <tr>
                  @if (d.scope.kind === 'Fund') {
                    <td><a [routerLink]="['/sleeves', check.sleeveId]">{{ check.sleeve }}</a></td>
                  }
                  <td>{{ check.rule }}</td>
                  <td class="num">{{ figure(check, check.current) }}</td>
                  <td class="num">{{ figure(check, check.limit) }}</td>
                  <td>
                    @if (check.withinLimit === null) {
                      <nz-tag>No data</nz-tag>
                    } @else if (check.withinLimit) {
                      <nz-tag nzColor="green">Within</nz-tag>
                    } @else {
                      <nz-tag nzColor="red">Breach</nz-tag>
                    }
                  </td>
                </tr>
              }
            </tbody>
          </nz-table>
          <p class="note">Shares are of the sleeve's own NAV. Days to liquidate assume trading 10% of average daily volume.</p>
        </div>
      </div>

      @if (d.scenarioByChild.length > 0) {
        <div class="panel">
          <h3>Scenario by {{ d.scope.kind === 'Fund' ? 'sleeve' : 'manager' }} <span class="hint">Betas as measured</span></h3>
          <table class="matrix">
            <tr>
              <th class="row"></th><th>NAV</th>
              @for (shock of d.scenario.shocks; track shock) {
                <th>{{ d.scenario.referenceAsset }} {{ shock | pct: 0 }}</th>
              }
            </tr>
            @for (row of d.scenarioByChild; track row.id) {
              <tr>
                <th class="row"><a [routerLink]="[row.kind === 'Sleeve' ? '/sleeves' : '/managers', row.id]">{{ row.name }}</a></th>
                <td>{{ row.navUsd | usd }}</td>
                @for (cell of row.pnl; track $index) {
                  <td [style]="heatStyle(cell)">{{ cell | usd: 2 }}</td>
                }
              </tr>
            }
          </table>
        </div>
      }

      <div class="cols" style="grid-template-columns:1.6fr 1fr">
        <div class="panel">
          <h3>Rolling volatility <span class="hint">30-day, annualised</span></h3>
          @if (d.rollingDates.length > 0) {
            <app-ria-chart [labels]="d.rollingDates" [series]="rolling()" yFormat="percent" [height]="260" [ariaLabel]="'Rolling volatility of ' + d.scope.name" testId="volatility-chart" />
          } @else {
            <p class="note">Not enough history yet for a 30-day window.</p>
          }
        </div>
        <div class="panel">
          <h3>Volatility</h3>
          <nz-table [nzData]="d.volatility" nzTemplateMode [nzShowPagination]="false" nzSize="small">
            <thead><tr><th>Name</th><th class="num">30 days</th><th class="num">90 days</th><th class="num">Share of variance</th></tr></thead>
            <tbody>
              @for (row of d.volatility; track row.kind + row.id; let first = $first) {
                <tr [class.total-row]="first">
                  <td>{{ row.name }}</td>
                  <td class="num">{{ row.days30 | pct: 1 : false }}</td>
                  <td class="num">{{ row.days90 | pct: 1 : false }}</td>
                  <td class="num">{{ first ? '' : (row.shareOfVariance | pct: 0 : false) }}</td>
                </tr>
              }
            </tbody>
          </nz-table>
          <p class="note">Share of variance: how much of the swings of {{ d.scope.name }} each part accounts for, at today's weights.</p>
        </div>
      </div>

      <div class="cols" [style.grid-template-columns]="d.correlation.names.length > 1 ? '1.5fr 1fr' : '1fr'">
        @if (d.correlation.names.length > 1) {
          <div class="panel" style="overflow-x:auto" data-testid="correlation">
            <h3>Manager correlation <span class="hint">Daily returns since each pair began</span></h3>
            <table class="matrix tight">
              <tr>
                <th class="row"></th>
                @for (name of d.correlation.names; track name; let i = $index) {
                  <th [title]="name">{{ i + 1 }}</th>
                }
              </tr>
              @for (row of d.correlation.matrix; track $index; let i = $index) {
                <tr>
                  <th class="row">{{ i + 1 }} · {{ d.correlation.names[i] }}</th>
                  @for (value of row; track $index) {
                    <td [style]="value === null ? {} : correlationStyle(value)">{{ value === null ? '–' : (value | number: '1.2-2') }}</td>
                  }
                </tr>
              }
            </table>
          </div>
        }
        <div class="panel">
          <h3>Exposure and beta by asset</h3>
          <nz-table [nzData]="d.assets" nzTemplateMode [nzShowPagination]="false" nzSize="small" [nzScroll]="{ y: '300px' }">
            <thead><tr><th>Asset</th><th class="num">Net</th><th class="num">Gross</th><th class="num">Beta to {{ d.scenario.referenceAsset }}</th></tr></thead>
            <tbody>
              @for (row of d.assets; track row.asset) {
                <tr>
                  <td><a [routerLink]="['/funds', d.scope.fundId, 'assets', row.asset]"><b>{{ row.asset }}</b></a></td>
                  <td class="num" [class]="row.netUsd | tone">{{ row.netUsd | usd: 2 }}</td>
                  <td class="num">{{ row.grossUsd | usd: 2 }}</td>
                  <td class="num">{{ row.beta | number: '1.2-2' }}{{ row.betaIsAssumed ? ' (assumed)' : '' }}</td>
                </tr>
              }
            </tbody>
          </nz-table>
        </div>
      </div>
    }
  `,
  styles: `
    .tight td {
      min-width: 44px;
      padding: 6px 4px;
    }
    .note {
      color: var(--kft-text-3);
      font-size: 12px;
      margin: 8px 0 0;
    }
  `,
})
export class RiskPanel {
  private readonly api = inject(RiskApi);

  readonly scope = input.required<PerformanceScope>();
  readonly scopeId = input.required<number>();

  private readonly loaded = keptData(
    combineLatest([toObservable(this.scope), toObservable(this.scopeId)]),
    toObservable(inject(LiveService).slowTick),
    ([scope, id]) => this.api.risk(scope, id),
    ([scope, id]) => `${scope}:${id}`,
  );
  protected readonly failed = this.loaded.failed;
  protected readonly missing = this.loaded.missing;
  protected readonly data = this.loaded.data;

  protected readonly breaches = computed(() => (this.data()?.mandate ?? []).filter((m) => m.withinLimit === false).length);
  protected readonly rolling = computed<RiaChartSeries[]>(() => {
    const d = this.data();
    if (!d) {
      return [];
    }

    let child = 0;
    return d.rollingVolatility.map((s, i) => {
      if (i === 0) {
        return { label: s.name, data: s.values, color: '#0e7f4c', width: 2.4 };
      }

      return s.name === d.scenario.referenceAsset && i === d.rollingVolatility.length - 1
        ? { label: s.name, data: s.values, color: '#98a2b3', dash: [5, 4] }
        : { label: s.name, data: s.values, color: CHILD_COLORS[child++ % CHILD_COLORS.length], width: 1.4 };
    });
  });
  private readonly scenarioMax = computed(() => {
    const d = this.data();
    return d ? Math.max(1, ...d.scenario.cells.flat().map(Math.abs)) : 1;
  });

  protected heatStyle(value: number): Record<string, string> {
    return heat(value, this.scenarioMax());
  }

  protected correlationStyle(value: number): Record<string, string> {
    return heat(value, 1);
  }

  protected leverage(gross: number, nav: number): string {
    return nav > 0 ? `${(gross / nav).toFixed(2)}×` : '–';
  }

  /** Days-to-liquidate limits are in days; every other limit is a share of NAV. */
  protected figure(check: MandateCheck, value: number | null): string {
    if (value === null) {
      return '–';
    }

    return check.kind === 'DaysToLiquidateMax' ? `${value.toFixed(1)} d` : pct(value, 1, false);
  }
}
