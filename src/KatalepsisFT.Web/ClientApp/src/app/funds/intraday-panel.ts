import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { catchError, combineLatest, of, switchMap } from 'rxjs';
import { LiveService } from '../core/live.service';
import { FORMAT_PIPES } from '../shared/format';
import { RiaChartComponent, RiaChartSeries } from '../shared/ria-chart.component';
import { FundsApi } from './funds.api';

/** The fund's NAV over the last 24 hours in five-minute steps. Shows nothing for a fund that is not read from venues. */
@Component({
  selector: 'app-intraday-panel',
  imports: [RiaChartComponent, ...FORMAT_PIPES],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (data(); as d) {
      @if (d.times.length >= 2) {
        <div class="panel" data-testid="intraday">
          <h3>
            Last 24 hours
            <span class="hint">NAV every five minutes, UTC · now {{ d.navUsd[d.navUsd.length - 1] | usd: 2 }}, <span [class]="change() | tone">{{ change() | pct: 2 }}</span> over the period shown</span>
          </h3>
          <app-ria-chart [labels]="labels()" [series]="series()" yFormat="usd" [height]="180" ariaLabel="Net asset value over the last 24 hours" testId="intraday-chart" />
        </div>
      }
    }
  `,
})
export class IntradayPanel {
  private readonly api = inject(FundsApi);

  readonly fundId = input.required<number>();

  protected readonly data = toSignal(
    combineLatest([toObservable(this.fundId), toObservable(inject(LiveService).tick)]).pipe(switchMap(([fundId]) => this.api.intraday(fundId, 24).pipe(catchError(() => of(null))))),
    { initialValue: null },
  );
  /** "14:35"; the date is added where the day changes, so a line crossing midnight stays readable. */
  protected readonly labels = computed(() =>
    (this.data()?.times ?? []).map((iso, index, all) => {
      const time = iso.slice(11, 16);
      return index > 0 && iso.slice(0, 10) !== all[index - 1].slice(0, 10) ? `${iso.slice(8, 10)}/${iso.slice(5, 7)} ${time}` : time;
    }),
  );
  protected readonly series = computed<RiaChartSeries[]>(() => [{ label: 'NAV', data: this.data()?.navUsd ?? [], color: '#0d366b', width: 2 }]);
  protected readonly change = computed(() => {
    const nav = this.data()?.navUsd ?? [];
    return nav.length >= 2 && nav[0] !== 0 ? nav[nav.length - 1] / nav[0] - 1 : null;
  });
}
