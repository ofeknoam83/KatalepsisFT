import { DatePipe } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { toObservable } from '@angular/core/rxjs-interop';
import { NzAlertModule } from 'ng-zorro-antd/alert';
import { NzButtonModule } from 'ng-zorro-antd/button';
import { NzPopconfirmModule } from 'ng-zorro-antd/popconfirm';
import { Observable } from 'rxjs';
import { LiveService } from '../core/live.service';
import { FORMAT_PIPES } from '../shared/format';
import { keptData } from '../shared/page-data';

interface TrackRecord {
  managerId: number;
  manager: string;
  canEdit: boolean;
  days: number;
  from: string | null;
  to: string | null;
  firstNavUsd: number | null;
  lastNavUsd: number | null;
  totalReturn: number | null;
  firstLiveDay: string | null;
  firstLiveNavUsd: number | null;
  joinReturn: number | null;
}

/**
 * A manager's history from before this system read it: a CSV of one NAV per day, with the money that moved in or
 * out. Importing it gives the manager, its sleeve and the fund their performance, drawdown and volatility for those
 * days. Admin only.
 */
@Component({
  selector: 'kft-track-record',
  imports: [DatePipe, NzAlertModule, NzButtonModule, NzPopconfirmModule, ...FORMAT_PIPES],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (errors().length > 0) {
      <nz-alert
        nzType="error"
        nzShowIcon
        nzCloseable
        (nzOnClose)="errors.set([])"
        [nzMessage]="errors().length === 1 ? errors()[0] : 'Nothing was changed. ' + errors().length + ' problems:'"
        [nzDescription]="errors().length > 1 ? list : null"
        style="display:block;margin-bottom:14px"
        data-testid="track-errors"
      />
      <ng-template #list>
        <ul style="margin:0;padding-left:18px">
          @for (e of errors(); track $index) {
            <li>{{ e }}</li>
          }
        </ul>
      </ng-template>
    }
    @if (saved(); as message) {
      <nz-alert nzType="success" [nzMessage]="message" nzShowIcon nzCloseable (nzOnClose)="saved.set(null)" style="display:block;margin-bottom:14px" data-testid="track-saved" />
    }
    @if (loaded.failed()) {
      <nz-alert nzType="error" nzMessage="Could not load this manager's track record." nzShowIcon />
    }
    @if (loaded.data(); as t) {
      <div class="panel">
        <h3>
          Track record
          <span class="hint">History from before this manager was read here: one NAV per day</span>
          @if (t.canEdit) {
            <button nz-button nzType="primary" nzSize="small" style="margin-left:12px" (click)="file.click()" data-testid="track-upload">{{ t.days > 0 ? 'Replace with a CSV file' : 'Import a CSV file' }}</button>
            <button nz-button nzSize="small" nzType="link" (click)="downloadTemplate()">Download a template</button>
            <input #file type="file" accept=".csv,text/csv" hidden (change)="upload($event)" data-testid="track-input" />
          }
        </h3>

        @if (t.days > 0) {
          <div class="tiles" data-testid="track-summary">
            <div class="tile"><div class="k">Imported days</div><div class="v">{{ t.days }}</div><div class="d">{{ t.from | date: 'd MMM y' : 'UTC' }} to {{ t.to | date: 'd MMM y' : 'UTC' }}</div></div>
            <div class="tile"><div class="k">Return over those days</div><div class="v" [class]="t.totalReturn | tone">{{ t.totalReturn | pct: 2 }}</div><div class="d">Money paid in or out is not counted as return</div></div>
            <div class="tile"><div class="k">First imported NAV</div><div class="v">{{ t.firstNavUsd | usd }}</div><div class="d">{{ t.from | date: 'd MMM y' : 'UTC' }}</div></div>
            <div class="tile"><div class="k">Last imported NAV</div><div class="v">{{ t.lastNavUsd | usd }}</div><div class="d">{{ t.to | date: 'd MMM y' : 'UTC' }}</div></div>
          </div>
          @if (t.firstLiveDay) {
            <nz-alert
              [nzType]="bigGap(t.joinReturn) ? 'warning' : 'info'"
              nzShowIcon
              [nzMessage]="
                'Live readings take over on ' +
                (t.firstLiveDay | date: 'd MMM y' : 'UTC') +
                ' at ' +
                (t.firstLiveNavUsd | usd) +
                ': ' +
                (t.joinReturn | pct: 2) +
                ' against the last imported NAV.' +
                (bigGap(t.joinReturn) ? ' That is a large step for one day. If the file and the live accounts do not describe the same book, correct the file: this step counts as performance.' : '')
              "
              style="display:block;margin-bottom:12px"
              data-testid="track-join"
            />
          } @else {
            <nz-alert nzType="info" nzShowIcon nzMessage="This manager is not read live yet. When its first key, wallet or positions are added, their value carries on from the last imported NAV: the difference counts as that day's performance." style="display:block;margin-bottom:12px" />
          }
          @if (t.canEdit) {
            <a style="color:#cf1322" nz-popconfirm nzPopconfirmTitle="Remove the imported history? Live readings stay." nzOkText="Remove" (nzOnConfirm)="run(http.delete('/api/managers/' + managerId() + '/track-record'), 'The imported history was removed.')" data-testid="track-remove">Remove the imported history</a>
          }
        } @else {
          <p style="color:#595959;margin:4px 0 10px">Nothing has been imported for this manager.</p>
        }

        <div class="how">
          <b>The file</b>
          <ul>
            <li>Three columns: <code>date,nav,flow</code>. One line per day; days without a line keep the previous NAV.</li>
            <li><code>date</code> as year-month-day (2025-03-31). <code>nav</code> is the manager's NAV in USD at the end of that day.</li>
            <li><code>flow</code> is money paid in (positive) or taken out (negative) that day, already included in that day's NAV. Leave it empty when none moved; without it a deposit would read as a gain.</li>
            <li>
              Every day must be in the past and before the manager's first live reading
              @if (t.firstLiveDay) {
                ({{ t.firstLiveDay | date: 'd MMM y' : 'UTC' }})
              }
              . A new file replaces the one imported before. If any line is wrong, nothing is changed and the lines are listed.
            </li>
            <li>Imported days carry a NAV only, no positions: they feed performance, drawdown and volatility, not holdings or exposure.</li>
          </ul>
        </div>
      </div>
    }
  `,
  styles: `
    .how {
      color: #595959;
      font-size: 12px;
      margin-top: 14px;

      ul {
        margin: 4px 0 0;
        padding-left: 18px;
      }

      li {
        margin-bottom: 3px;
      }
    }
  `,
})
export class TrackRecordPanel {
  readonly managerId = input.required<number>();

  protected readonly http = inject(HttpClient);
  private readonly live = inject(LiveService);
  protected readonly errors = signal<string[]>([]);
  protected readonly saved = signal<string | null>(null);
  private readonly version = signal(0);

  protected readonly loaded = keptData(
    toObservable(this.managerId),
    toObservable(this.version),
    (id) => this.http.get<TrackRecord>(`/api/managers/${id}/track-record`),
    (id) => id,
  );

  /** More than a tenth in one day between the last imported NAV and the first live one. */
  protected bigGap(step: number | null): boolean {
    return step !== null && Math.abs(step) > 0.1;
  }

  protected upload(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) {
      return;
    }

    void file.text().then((csv) => this.run(this.http.post(`/api/managers/${this.managerId()}/track-record`, { csv }), `${file.name} was imported.`));
  }

  protected downloadTemplate(): void {
    const day = (back: number): string => new Date(Date.now() - back * 86_400_000).toISOString().slice(0, 10);
    const lines = ['date,nav,flow', `${day(4)},1000000,`, `${day(3)},1012500,`, `${day(2)},1262000,250000`, `${day(1)},1259300,`];
    const link = document.createElement('a');
    link.href = URL.createObjectURL(new Blob([lines.join('\n') + '\n'], { type: 'text/csv' }));
    link.download = 'track-record-template.csv';
    link.click();
    URL.revokeObjectURL(link.href);
  }

  protected run(request: Observable<unknown>, message: string): void {
    this.errors.set([]);
    this.saved.set(null);
    request.subscribe({
      next: () => {
        this.saved.set(message);
        this.version.update((v) => v + 1);
        // The manager's own figures, shown above, now reach further back.
        this.live.refresh();
      },
      error: (e: { error?: { errors?: string[]; message?: string } }) => this.errors.set(e.error?.errors ?? [e.error?.message ?? 'That did not work. Try again.']),
    });
  }
}
