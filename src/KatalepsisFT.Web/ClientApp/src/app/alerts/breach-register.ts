import { DatePipe } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { toObservable } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { NzAlertModule } from 'ng-zorro-antd/alert';
import { NzButtonModule } from 'ng-zorro-antd/button';
import { NzTableModule } from 'ng-zorro-antd/table';
import { NzTagModule } from 'ng-zorro-antd/tag';
import { combineLatest } from 'rxjs';
import { keptData } from '../shared/page-data';
import { AlertMetric, AlertSeverity, AlertStatus } from './models';

interface Breach {
  id: number;
  severity: AlertSeverity;
  scopeKind: string;
  scopeName: string;
  rule: string;
  subject: string | null;
  fromMandate: boolean;
  metric: AlertMetric;
  raisedAt: string;
  endedAt: string | null;
  durationHours: number;
  threshold: number;
  peakValue: number;
  lastValue: number;
  status: AlertStatus;
  acknowledgedBy: string | null;
  acknowledgedAt: string | null;
  outcome: string;
  note: string | null;
}

interface BreachRegister {
  from: string;
  to: string;
  breaches: number;
  mandateBreaches: number;
  stillOpen: number;
  rows: Breach[];
}

const SEVERITY_COLOR = { Critical: 'red', Warning: 'orange', Info: 'blue' } as const;

/** Days back from today as a year-month-day text, in UTC like every date the server keeps. */
function day(back: number): string {
  return new Date(Date.now() - back * 86_400_000).toISOString().slice(0, 10);
}

/**
 * The breach register: every time a rule or mandate limit was broken in a period, how far, for how long, who
 * acknowledged it and how it ended. It is the record to hand to a board, an investor or an auditor.
 */
@Component({
  selector: 'kft-breach-register',
  imports: [DatePipe, FormsModule, NzAlertModule, NzButtonModule, NzTableModule, NzTagModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="panel">
      <h3>
        Breach register
        <span class="hint">Every breach that was active at any time in the period</span>
      </h3>
      <div class="bar">
        <label>From <input type="date" [ngModel]="from()" (ngModelChange)="from.set($event)" [max]="to()" data-testid="breach-from" /></label>
        <label>To <input type="date" [ngModel]="to()" (ngModelChange)="to.set($event)" [min]="from()" data-testid="breach-to" /></label>
        <span class="quick"><a (click)="last(30)">30 days</a><a (click)="last(90)">90 days</a><a (click)="last(365)">1 year</a></span>
        <span style="flex:1"></span>
        <a nz-button nzSize="small" [href]="csvUrl()" data-testid="breach-csv">Download CSV</a>
      </div>
      @if (loaded.failed()) {
        <nz-alert nzType="error" nzMessage="Could not load the breach register." nzShowIcon />
      }
      @if (loaded.data(); as r) {
        <p class="sum" data-testid="breach-summary">
          <b>{{ r.breaches }}</b> {{ r.breaches === 1 ? 'breach' : 'breaches' }} · <b>{{ r.mandateBreaches }}</b> of mandate limits · <b>{{ r.stillOpen }}</b> still open
        </p>
        <nz-table [nzData]="r.rows" nzTemplateMode [nzShowPagination]="false" nzSize="small" data-testid="breach-table">
          <thead>
            <tr>
              <th>Raised (UTC)</th><th>Where</th><th>Rule</th><th class="num">Limit</th><th class="num">Peak</th><th class="num">Lasted</th><th>Acknowledged</th><th>How it ended</th>
            </tr>
          </thead>
          <tbody>
            @for (b of r.rows; track b.id) {
              <tr>
                <td class="nowrap">{{ b.raisedAt | date: 'MM-dd-yyyy HH:mm' : 'UTC' }}</td>
                <td><b>{{ b.scopeName }}</b> <span class="soft">· {{ b.scopeKind }}</span></td>
                <td>
                  <nz-tag [nzColor]="severityColor[b.severity]">{{ b.severity }}</nz-tag>
                  @if (b.fromMandate) {
                    <nz-tag>Mandate</nz-tag>
                  }
                  {{ b.rule }}
                  @if (b.subject) {
                    <span class="soft">· {{ b.subject }}</span>
                  }
                </td>
                <td class="num">{{ figure(b, b.threshold) }}</td>
                <td class="num"><b>{{ figure(b, b.peakValue) }}</b></td>
                <td class="num nowrap">{{ lasted(b.durationHours) }}</td>
                <td>
                  @if (b.acknowledgedBy) {
                    {{ b.acknowledgedBy }} <span class="soft">· {{ b.acknowledgedAt | date: 'MM-dd HH:mm' : 'UTC' }}</span>
                  } @else {
                    <span class="soft">–</span>
                  }
                </td>
                <td>
                  <span [class.open]="!b.endedAt">{{ b.outcome }}</span>
                  @if (b.endedAt) {
                    <span class="soft"> · {{ b.endedAt | date: 'MM-dd-yyyy HH:mm' : 'UTC' }}</span>
                  }
                  @if (b.note) {
                    <div class="soft" style="max-width:300px">“{{ b.note }}”</div>
                  }
                </td>
              </tr>
            }
            @if (r.rows.length === 0) {
              <tr><td colspan="8" style="text-align:center;color:var(--kft-text-3);padding:28px">No rule or limit was broken in this period.</td></tr>
            }
          </tbody>
        </nz-table>
      }
    </div>
  `,
  styles: `
    .bar {
      display: flex;
      align-items: center;
      gap: 14px;
      flex-wrap: wrap;
      margin-bottom: 10px;
      font-size: 13px;
      color: var(--kft-text-2);

      input {
        margin-left: 4px;
        padding: 2px 6px;
        border: 1px solid #d5d9e0;
        border-radius: 4px;
        font: inherit;
      }
    }

    .quick a {
      margin-right: 10px;
    }

    .sum {
      margin: 0 0 10px;
      color: var(--kft-text-2);
    }

    .soft {
      color: var(--kft-text-3);
      font-size: 12px;
    }

    .open {
      color: #cf1322;
      font-weight: 500;
    }
  `,
})
export class BreachRegisterPanel {
  readonly fundId = input.required<number>();

  private readonly http = inject(HttpClient);
  protected readonly severityColor = SEVERITY_COLOR;
  protected readonly from = signal(day(90));
  protected readonly to = signal(day(0));
  protected readonly csvUrl = computed(() => `/api/funds/${this.fundId()}/breaches?from=${this.from()}&to=${this.to()}&format=csv`);

  protected readonly loaded = keptData(
    combineLatest([toObservable(this.fundId), toObservable(this.from), toObservable(this.to)]),
    toObservable(signal(0)),
    ([fundId, from, to]) => this.http.get<BreachRegister>(`/api/funds/${fundId}/breaches?from=${from}&to=${to}`),
    ([fundId]) => fundId,
  );

  protected last(days: number): void {
    this.to.set(day(0));
    this.from.set(day(days));
  }

  /** A limit or a figure as it is read: days for time to liquidate, a percentage for everything else. */
  protected figure(breach: Breach, value: number): string {
    return breach.metric === 'SlowestDaysToLiquidate' ? `${value.toFixed(1)} days` : `${(value * 100).toFixed(1).replace('-', '−')}%`;
  }

  protected lasted(hours: number): string {
    if (hours < 1) {
      return `${Math.max(1, Math.round(hours * 60))} min`;
    }

    return hours < 48 ? `${hours.toFixed(1)} h` : `${(hours / 24).toFixed(1)} days`;
  }
}
