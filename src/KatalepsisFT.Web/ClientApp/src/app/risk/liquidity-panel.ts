import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { NzAlertModule } from 'ng-zorro-antd/alert';
import { NzSegmentedModule } from 'ng-zorro-antd/segmented';
import { NzSpinModule } from 'ng-zorro-antd/spin';
import { NzTableModule } from 'ng-zorro-antd/table';
import { catchError, combineLatest, of, switchMap, tap } from 'rxjs';
import { PerformanceScope } from '../performance/models';
import { FORMAT_PIPES } from '../shared/format';
import { Ladder } from './models';
import { RiskApi } from './risk.api';

interface Bucket {
  label: string;
  usd: number;
  color: string;
}

/** How long it would take to turn a fund, a sleeve or a manager into cash at a chosen share of market volume. */
@Component({
  selector: 'app-liquidity-panel',
  imports: [DecimalPipe, FormsModule, RouterLink, NzAlertModule, NzSegmentedModule, NzSpinModule, NzTableModule, ...FORMAT_PIPES],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div style="display:flex;align-items:center;gap:10px;margin-bottom:14px">
      <span style="color:#595959;font-size:13px">Share of average daily volume we trade</span>
      <nz-segmented [nzOptions]="options" [ngModel]="participation()" (nzValueChange)="participation.set(+$event)" data-testid="participation" />
    </div>
    @if (failed()) {
      <nz-alert nzType="error" nzMessage="Could not load the liquidity figures." nzShowIcon />
    } @else if (data() === null) {
      <nz-spin nzSimple style="display:block;margin:60px auto" />
    }
    @if (data(); as d) {
      <div class="tiles">
        <div class="tile"><div class="k">Within 1 day</div><div class="v">{{ d.ladder.withinOneDayUsd | share: d.ladder.totalUsd }}</div><div class="d">{{ d.ladder.withinOneDayUsd | usd }} including cash</div></div>
        <div class="tile"><div class="k">Within 5 days</div><div class="v">{{ d.ladder.withinOneDayUsd + d.ladder.oneToFiveDaysUsd | share: d.ladder.totalUsd }}</div><div class="d">{{ d.ladder.withinOneDayUsd + d.ladder.oneToFiveDaysUsd | usd }}</div></div>
        <div class="tile"><div class="k">Needs more than 20 days</div><div class="v" [class]="d.ladder.beyondTwentyDaysUsd > 0 ? 'down' : ''">{{ d.ladder.beyondTwentyDaysUsd | usd }}</div><div class="d">{{ d.ladder.beyondTwentyDaysUsd | share: d.ladder.totalUsd }} of the total</div></div>
        <div class="tile"><div class="k">Slowest asset</div><div class="v">{{ slowest(d.lines)?.asset ?? '–' }}</div><div class="d">{{ slowest(d.lines)?.days === undefined ? '' : (slowest(d.lines)!.days | number: '1.1-1') + ' days to close' }}</div></div>
      </div>

      <div class="panel" data-testid="ladder">
        <h3>Liquidity ladder <span class="hint">Cash plus the exposure that can be closed in each window</span></h3>
        <table style="width:100%;border-collapse:collapse;font-size:13px">
          @for (row of rows(d); track row.name; let first = $first) {
            <tr>
              <td style="width:170px;padding:6px 0" [style.font-weight]="first ? 600 : 400">
                @if (row.link) {
                  <a [routerLink]="row.link">{{ row.name }}</a>
                } @else {
                  {{ row.name }}
                }
              </td>
              <td style="padding:6px 0">
                <div class="bar">
                  @for (b of buckets(row.ladder); track b.label) {
                    @if (b.usd > 0) {
                      <span [style.flex-grow]="b.usd" [style.background]="b.color" [title]="b.label + ': ' + (b.usd | usd: 2)">{{ b.usd / row.ladder.totalUsd >= 0.07 ? (b.usd | share: row.ladder.totalUsd : 0) : '' }}</span>
                    }
                  }
                </div>
              </td>
              <td class="num" style="width:90px;padding:6px 0 6px 12px">{{ row.ladder.totalUsd | usd }}</td>
            </tr>
          }
        </table>
        <div class="legend">
          @for (b of buckets(d.ladder); track b.label) {
            <span><i [style.background]="b.color"></i>{{ b.label }}</span>
          }
        </div>
        @if (d.children.length > 0) {
          <p class="note">Each {{ d.scope.kind === 'Fund' ? 'sleeve' : 'manager' }} is timed as if it sold alone. The top bar times them selling together, competing for the same volume.</p>
        }
      </div>

      <div class="cols" style="grid-template-columns:1.5fr 1fr">
        <div class="panel">
          <h3>Days to liquidate by asset <span class="hint">At {{ d.participation | pct: 0 : false }} of 30-day average daily volume</span></h3>
          <nz-table [nzData]="d.lines" nzTemplateMode [nzShowPagination]="false" nzSize="small">
            <thead><tr><th>Asset</th><th class="num">Gross exposure</th><th class="num">Avg daily volume</th><th class="num">We can trade / day</th><th class="num">Days</th><th class="num">Managers</th></tr></thead>
            <tbody>
              @for (line of d.lines; track line.asset) {
                <tr>
                  <td><a [routerLink]="['/funds', d.scope.fundId, 'assets', line.asset]"><b>{{ line.asset }}</b></a></td>
                  <td class="num">{{ line.grossUsd | usd: 2 }}</td>
                  <td class="num">{{ line.averageDailyVolumeUsd | usd }}</td>
                  <td class="num">{{ line.dailyCapacityUsd | usd }}</td>
                  <td class="num" [class.down]="(line.days ?? 0) > 5"><b>{{ line.days === null ? 'No volume data' : (line.days | number: '1.2-2') }}</b></td>
                  <td class="num">{{ line.managers }}</td>
                </tr>
              }
            </tbody>
          </nz-table>
        </div>
        <div class="panel" data-testid="queue">
          <h3>Who holds {{ slowest(d.lines)?.asset ?? 'the slowest asset' }} <span class="hint">Days each position would need alone</span></h3>
          <nz-table [nzData]="d.slowestQueue" nzTemplateMode [nzShowPagination]="false" nzSize="small">
            <thead><tr><th>Manager</th><th>Instrument</th><th class="num">Exposure</th><th class="num">Days alone</th></tr></thead>
            <tbody>
              @for (q of d.slowestQueue; track q.manager + q.instrument) {
                <tr>
                  <td>{{ q.manager }}</td>
                  <td>{{ q.instrument }}</td>
                  <td class="num" [class]="q.exposureUsd | tone">{{ q.exposureUsd | usd: 2 }}</td>
                  <td class="num">{{ q.daysAlone === null ? '–' : (q.daysAlone | number: '1.2-2') }}</td>
                </tr>
              }
            </tbody>
          </nz-table>
          <p class="note">When several managers hold the same asset they would be selling into the same market, so together they need the sum of these days.</p>
        </div>
      </div>
    }
  `,
  styles: `
    .bar {
      display: flex;
      height: 22px;
      border-radius: 4px;
      overflow: hidden;
      background: #f0efec;
    }
    .bar span {
      color: #fff;
      font-size: 11px;
      line-height: 22px;
      text-align: center;
      white-space: nowrap;
      overflow: hidden;
      min-width: 2px;
    }
    .legend {
      display: flex;
      gap: 18px;
      margin-top: 10px;
      font-size: 12px;
      color: #595959;
    }
    .legend i {
      display: inline-block;
      width: 10px;
      height: 10px;
      border-radius: 2px;
      margin-right: 6px;
    }
    .note {
      color: #8c8c8c;
      font-size: 12px;
      margin: 8px 0 0;
    }
  `,
})
export class LiquidityPanel {
  private readonly api = inject(RiskApi);

  readonly scope = input.required<PerformanceScope>();
  readonly scopeId = input.required<number>();

  protected readonly options = [
    { label: '5%', value: 0.05 },
    { label: '10%', value: 0.1 },
    { label: '20%', value: 0.2 },
    { label: '33%', value: 0.33 },
  ];
  protected readonly participation = signal(0.1);
  protected readonly failed = signal(false);
  protected readonly data = toSignal(
    combineLatest([toObservable(this.scope), toObservable(this.scopeId), toObservable(this.participation)]).pipe(
      switchMap(([scope, id, participation]) =>
        this.api.liquidity(scope, id, participation).pipe(
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

  protected rows(d: NonNullable<ReturnType<typeof this.data>>): { name: string; ladder: Ladder; link: unknown[] | null }[] {
    return [{ name: d.scope.name, ladder: d.ladder, link: null }, ...d.children.map((c) => ({ name: c.name, ladder: c.ladder, link: [c.kind === 'Sleeve' ? '/sleeves' : '/managers', c.id] }))];
  }

  protected buckets(ladder: Ladder): Bucket[] {
    return [
      { label: 'Within 1 day', usd: ladder.withinOneDayUsd, color: '#1f6fd0' },
      { label: '1 to 5 days', usd: ladder.oneToFiveDaysUsd, color: '#6aa5e8' },
      { label: '5 to 20 days', usd: ladder.fiveToTwentyDaysUsd, color: '#e08a00' },
      { label: 'More than 20 days', usd: ladder.beyondTwentyDaysUsd, color: '#d03b3b' },
      { label: 'No volume data', usd: ladder.unknownUsd, color: '#a3a19c' },
    ];
  }

  protected slowest<T extends { days: number | null }>(lines: T[]): T | undefined {
    return lines.find((l) => l.days !== null);
  }
}
