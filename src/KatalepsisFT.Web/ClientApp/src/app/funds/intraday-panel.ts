import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, input, signal } from '@angular/core';
import { toObservable } from '@angular/core/rxjs-interop';
import { combineLatest } from 'rxjs';
import { LiveService } from '../core/live.service';
import { FORMAT_PIPES } from '../shared/format';
import { keptData } from '../shared/page-data';
import { RiaChartComponent, RiaChartSeries } from '../shared/ria-chart.component';
import { FundsApi } from './funds.api';

/**
 * The fund's NAV over the last hour in ten-second steps, or over the last 24 hours in five-minute steps. Shows nothing
 * for a fund that is not read from venues.
 */
@Component({
  selector: 'app-intraday-panel',
  imports: [RiaChartComponent, ...FORMAT_PIPES],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (data(); as d) {
      @if (d.times.length >= 2) {
        <div class="panel" data-testid="intraday">
          <h3>
            {{ hours() === 1 ? 'Last hour' : 'Last 24 hours' }}
            <span class="hint">NAV every {{ hours() === 1 ? '10 seconds' : 'five minutes' }}, UTC · now {{ d.navUsd[d.navUsd.length - 1] | usd: 2 }}, <span [class]="change() | tone">{{ change() | pct: 2 }}</span> over the period shown</span>
            <span class="window" data-testid="intraday-window">
              <button type="button" [class.on]="hours() === 1" (click)="hours.set(1)">1H</button>
              <button type="button" [class.on]="hours() === 24" (click)="hours.set(24)">24H</button>
            </span>
          </h3>
          <app-ria-chart [labels]="labels()" [series]="series()" yFormat="usd" [height]="180" [ariaLabel]="hours() === 1 ? 'Net asset value over the last hour' : 'Net asset value over the last 24 hours'" testId="intraday-chart" />
        </div>
      }
    }
  `,
  styles: `
    h3 { display: flex; align-items: baseline; gap: 8px; }
    .window { margin-left: auto; display: inline-flex; border: 1px solid var(--kft-border, #d9dee5); border-radius: 6px; overflow: hidden; }
    .window button { border: 0; background: transparent; padding: 2px 10px; font-size: 12px; font-weight: 500; color: var(--kft-text-3); cursor: pointer; }
    .window button.on { background: var(--kft-brand-soft, #e6f4ec); color: var(--kft-brand, #0e7f4c); }
  `,
})
export class IntradayPanel {
  private readonly api = inject(FundsApi);

  readonly fundId = input.required<number>();

  /** How far back the chart looks. An hour is drawn from ten-second samples, a day from five-minute ones. */
  protected readonly hours = signal<1 | 24>(1);

  /** Counts up every ten seconds, so the last hour gains its newest point as it is kept. */
  private readonly beat = signal(0);

  constructor() {
    const timer = setInterval(() => this.beat.update((b) => b + 1), 10_000);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }

  protected readonly data = keptData(
    combineLatest([toObservable(this.fundId), toObservable(this.hours)]),
    combineLatest([toObservable(inject(LiveService).slowTick), toObservable(this.beat)]),
    ([fundId, hours]) => this.api.intraday(fundId, hours),
    ([fundId, hours]) => fundId + ':' + hours,
  ).data;
  /** "14:35", or "14:35:20" for the last hour; the date is added where the day changes, so a line crossing midnight stays readable. */
  protected readonly labels = computed(() =>
    (this.data()?.times ?? []).map((iso, index, all) => {
      const time = iso.slice(11, this.hours() === 1 ? 19 : 16);
      return index > 0 && iso.slice(0, 10) !== all[index - 1].slice(0, 10) ? `${iso.slice(8, 10)}/${iso.slice(5, 7)} ${time}` : time;
    }),
  );
  protected readonly series = computed<RiaChartSeries[]>(() => [{ label: 'NAV', data: this.data()?.navUsd ?? [], color: '#0e7f4c', width: 2.2, fill: true }]);
  protected readonly change = computed(() => {
    const nav = this.data()?.navUsd ?? [];
    return nav.length >= 2 && nav[0] !== 0 ? nav[nav.length - 1] / nav[0] - 1 : null;
  });
}
