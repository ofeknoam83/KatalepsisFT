import { DatePipe } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { toObservable } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { Store } from '@ngxs/store';
import { NzAlertModule } from 'ng-zorro-antd/alert';
import { NzButtonModule } from 'ng-zorro-antd/button';
import { NzInputModule } from 'ng-zorro-antd/input';
import { NzPopconfirmModule } from 'ng-zorro-antd/popconfirm';
import { NzSelectModule } from 'ng-zorro-antd/select';
import { NzTableModule } from 'ng-zorro-antd/table';
import { NzTagModule } from 'ng-zorro-antd/tag';
import { Observable, of } from 'rxjs';
import { SessionState } from '../core/session.state';
import { PageHead } from '../shared/page-head';
import { keptData, pageData } from '../shared/page-data';
import { FundTabs } from './fund-tabs';
import { FundsApi } from './funds.api';

type ReportKind = 'DailyNav' | 'Factsheet';
type ReportFrequency = 'Daily' | 'Weekly' | 'Monthly';

interface Schedule {
  id: number;
  kind: ReportKind;
  frequency: ReportFrequency;
  hourUtc: number;
  recipients: string;
  isEnabled: boolean;
  lastSentOn: string | null;
  lastAttemptAt: string | null;
  lastError: string | null;
  createdBy: string;
}

interface Schedules {
  mailConfigured: boolean;
  schedules: Schedule[];
}

const KIND_LABEL: Record<ReportKind, string> = { DailyNav: 'Daily NAV sheet', Factsheet: 'Factsheet' };
const WHEN_LABEL: Record<ReportFrequency, string> = { Daily: 'Every day', Weekly: 'Every Monday', Monthly: 'On the 1st of each month' };

/** The fund's reports: open or download them here, and have them emailed on a schedule. */
@Component({
  selector: 'app-fund-reports-page',
  imports: [DatePipe, FormsModule, NzAlertModule, NzButtonModule, NzInputModule, NzPopconfirmModule, NzSelectModule, NzTableModule, NzTagModule, PageHead, FundTabs],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (page.failed()) {
      <nz-alert nzType="error" nzMessage="Could not load the fund." nzShowIcon />
    }
    @if (page.data(); as d) {
      <app-page-head [crumbs]="[{ label: d.fund.name, link: ['/funds', d.fund.id] }, { label: 'Reports' }]" [title]="d.fund.name" [renameUrl]="'/api/funds/' + d.fund.id + '/name'" renameWhat="the fund" [demo]="d.fund.isDemo" sub="Documents to send or file" [asOf]="d.asOf" [showFilter]="false" />
      <app-fund-tabs [fundId]="d.fund.id" />

      <div class="cards">
        <div class="panel card" data-testid="report-factsheet">
          <h3>Factsheet</h3>
          <p>One page: NAV, return against the benchmark, risk, sleeves and what each contributed, largest exposures, where the money sits, and breaches of limits in the last 30 days.</p>
          <a nz-button nzType="primary" [href]="'/api/funds/' + d.fund.id + '/reports/factsheet'" target="_blank" rel="noopener">Open the factsheet</a>
          <a nz-button nzType="link" [href]="'/api/funds/' + d.fund.id + '/reports/factsheet?download=true'">Download the page</a>
          <p class="note">It opens in a new tab. "Print or save as PDF" there gives a PDF on A4.</p>
        </div>
        <div class="panel card" data-testid="report-nav">
          <h3>Daily NAV sheet</h3>
          <p>An Excel workbook: NAV of the fund and of every manager for each of the last 120 days with flows and day returns, each manager's book today, and recent cash flows.</p>
          <a nz-button nzType="primary" [href]="'/api/funds/' + d.fund.id + '/reports/daily-nav'">Download the workbook</a>
          <p class="note">Amounts are in full; returns are time-weighted, so money paid in or out is not counted as performance.</p>
        </div>
      </div>

      @if (isAdmin()) {
        @if (error(); as message) {
          <nz-alert nzType="error" [nzMessage]="message" nzShowIcon nzCloseable (nzOnClose)="error.set(null)" style="display:block;margin-bottom:10px" data-testid="schedule-error" />
        }
        @if (saved(); as message) {
          <nz-alert nzType="success" [nzMessage]="message" nzShowIcon nzCloseable (nzOnClose)="saved.set(null)" style="display:block;margin-bottom:10px" data-testid="schedule-saved" />
        }
        @if (loaded.data(); as s) {
          <div class="panel">
            <h3>Sent by email <span class="hint">Each report goes out once on its day, from the hour given (UTC)</span></h3>
            @if (!s.mailConfigured) {
              <nz-alert
                nzType="warning"
                nzShowIcon
                nzMessage="No mail account is set up on the server yet, so nothing is sent."
                nzDescription="Schedules can be prepared now. To switch sending on, give the server a mail account: in the Render dashboard, open the environment group katalepsis-ft-secrets and set SMTP_HOST, SMTP_PORT, SMTP_USER, SMTP_PASSWORD and MAIL_FROM. Any provider's SMTP details work."
                style="display:block;margin-bottom:12px"
                data-testid="mail-missing"
              />
            }
            <nz-table [nzData]="s.schedules" nzTemplateMode [nzShowPagination]="false" nzSize="small" data-testid="schedules-table">
              <thead>
                <tr><th>Report</th><th>When</th><th>To</th><th>Last sent</th><th></th></tr>
              </thead>
              <tbody>
                @for (row of s.schedules; track row.id) {
                  <tr>
                    <td>
                      <b>{{ kindLabel[row.kind] }}</b>
                      @if (!row.isEnabled) {
                        <nz-tag style="margin-left:6px">Paused</nz-tag>
                      }
                    </td>
                    <td class="nowrap">{{ whenLabel[row.frequency] }} from {{ hour(row.hourUtc) }} UTC</td>
                    <td style="max-width:340px;overflow-wrap:anywhere">{{ row.recipients }}</td>
                    <td>
                      {{ row.lastSentOn ? (row.lastSentOn | date: 'd MMM y' : 'UTC') : 'Never' }}
                      @if (row.lastError) {
                        <div style="color:#cf1322;font-size:12px;max-width:300px">{{ row.lastError }}</div>
                      }
                    </td>
                    <td class="nowrap">
                      @if (sendingId() === row.id) {
                        <span style="color:var(--kft-text-3)">Sending…</span>
                      } @else {
                        <a (click)="sendNow(row.id, row.recipients)" [attr.data-testid]="'send-' + row.id">Send now</a>
                      }
                      <a style="margin-left:12px" (click)="run(http.put('/api/report-schedules/' + row.id, { kind: row.kind, frequency: row.frequency, hourUtc: row.hourUtc, recipients: row.recipients, isEnabled: !row.isEnabled }), row.isEnabled ? 'Paused.' : 'Switched on.')">{{ row.isEnabled ? 'Pause' : 'Switch on' }}</a>
                      <a style="margin-left:12px;color:#cf1322" nz-popconfirm nzPopconfirmTitle="Remove this schedule?" nzOkText="Remove" (nzOnConfirm)="run(http.delete('/api/report-schedules/' + row.id), 'Schedule removed.')">Remove</a>
                    </td>
                  </tr>
                }
                @if (s.schedules.length === 0) {
                  <tr><td colspan="5" style="text-align:center;color:var(--kft-text-3);padding:22px">Nothing is sent by email yet.</td></tr>
                }
              </tbody>
            </nz-table>

            <div class="add" data-testid="schedule-form">
              <nz-select [ngModel]="kind()" (ngModelChange)="kind.set($event)" style="width:170px" data-testid="schedule-kind">
                <nz-option nzValue="DailyNav" nzLabel="Daily NAV sheet" />
                <nz-option nzValue="Factsheet" nzLabel="Factsheet" />
              </nz-select>
              <nz-select [ngModel]="frequency()" (ngModelChange)="frequency.set($event)" style="width:210px">
                <nz-option nzValue="Daily" nzLabel="Every day" />
                <nz-option nzValue="Weekly" nzLabel="Every Monday" />
                <nz-option nzValue="Monthly" nzLabel="On the 1st of each month" />
              </nz-select>
              <nz-select [ngModel]="hourUtc()" (ngModelChange)="hourUtc.set($event)" style="width:130px" title="The hour, UTC, from which it goes out">
                @for (h of hours; track h) {
                  <nz-option [nzValue]="h" [nzLabel]="hour(h) + ' UTC'" />
                }
              </nz-select>
              <input nz-input [ngModel]="recipients()" (ngModelChange)="recipients.set($event)" placeholder="Email addresses, separated by commas" style="flex:1;min-width:240px" maxlength="2000" data-testid="schedule-recipients" />
              <button nz-button nzType="primary" [disabled]="recipients().trim() === ''" (click)="add(d.fund.id)" data-testid="schedule-add">Add a schedule</button>
            </div>
            <p class="note">The daily NAV sheet is sent as an attached workbook. The factsheet is sent as the email itself and as an attached page that prints to a PDF; some mail programs leave the chart out of the email body.</p>
          </div>
        }
      }
    }
  `,
  styles: `
    .cards {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(360px, 1fr));
      gap: 10px;
    }

    .card p {
      color: var(--kft-text-2);
      margin: 0 0 12px;
    }

    .note {
      color: var(--kft-text-3) !important;
      font-size: 12px;
      margin: 10px 0 0 !important;
    }

    .add {
      display: flex;
      gap: 8px;
      flex-wrap: wrap;
      margin-top: 12px;
    }
  `,
})
export class FundReportsPage {
  private readonly funds = inject(FundsApi);
  protected readonly http = inject(HttpClient);
  protected readonly isAdmin = inject(Store).selectSignal(SessionState.isAdmin);
  protected readonly kindLabel = KIND_LABEL;
  protected readonly whenLabel = WHEN_LABEL;
  protected readonly hours = Array.from({ length: 24 }, (_, h) => h);

  protected readonly page = pageData((params) => this.funds.overview(Number(params.get('fundId')), 'All'));
  protected readonly error = signal<string | null>(null);
  protected readonly saved = signal<string | null>(null);
  private readonly version = signal(0);
  private readonly fundId = computed(() => (this.isAdmin() ? (this.page.data()?.fund.id ?? null) : null));
  protected readonly loaded = keptData(
    toObservable(this.fundId),
    toObservable(this.version),
    (fundId) => (fundId === null ? of(null) : this.http.get<Schedules>(`/api/funds/${fundId}/report-schedules`)),
    (fundId) => fundId,
  );

  protected readonly kind = signal<ReportKind>('DailyNav');
  protected readonly frequency = signal<ReportFrequency>('Daily');
  protected readonly hourUtc = signal(7);
  protected readonly recipients = signal('');

  protected hour(h: number): string {
    return `${String(h).padStart(2, '0')}:00`;
  }

  protected add(fundId: number): void {
    const sent = this.recipients();
    this.run(this.http.post(`/api/funds/${fundId}/report-schedules`, { kind: this.kind(), frequency: this.frequency(), hourUtc: this.hourUtc(), recipients: sent, isEnabled: true }), 'Schedule added.', () => {
      // Only what was sent is cleared; anything typed since stays.
      if (this.recipients() === sent) {
        this.recipients.set('');
      }
    });
  }

  /** The schedule whose report is being sent right now, so a second click cannot send it twice. */
  protected readonly sendingId = signal<number | null>(null);

  protected sendNow(id: number, recipients: string): void {
    if (this.sendingId() !== null) {
      return;
    }

    this.sendingId.set(id);
    this.http.post('/api/report-schedules/' + id + '/send', {}).subscribe({
      next: () => {
        this.sendingId.set(null);
        this.error.set(null);
        this.saved.set('Sent to ' + recipients + '.');
        this.version.update((v) => v + 1);
      },
      error: (e: { error?: { message?: string } }) => {
        this.sendingId.set(null);
        this.saved.set(null);
        this.error.set(e.error?.message ?? 'That did not work. Try again.');
        this.version.update((v) => v + 1);
      },
    });
  }

  protected run(request: Observable<unknown>, message: string, done?: () => void): void {
    this.error.set(null);
    this.saved.set(null);
    request.subscribe({
      next: () => {
        done?.();
        this.saved.set(message);
        this.version.update((v) => v + 1);
      },
      error: (e: { error?: { message?: string } }) => {
        this.error.set(e.error?.message ?? 'That did not work. Try again.');
        // A failed send still leaves its reason on the schedule.
        this.version.update((v) => v + 1);
      },
    });
  }
}
