import { DatePipe } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { toObservable } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { Store } from '@ngxs/store';
import { NzAlertModule } from 'ng-zorro-antd/alert';
import { NzButtonModule } from 'ng-zorro-antd/button';
import { NzSelectModule } from 'ng-zorro-antd/select';
import { NzTableModule } from 'ng-zorro-antd/table';
import { combineLatest } from 'rxjs';
import { ViewState } from '../funds/view.state';
import { keptData } from '../shared/page-data';

interface AuditEntry {
  id: number;
  at: string;
  userName: string;
  action: string;
  fundId: number | null;
  fundName: string | null;
  subject: string | null;
  detail: string | null;
}

interface AuditList {
  from: string;
  to: string;
  truncated: boolean;
  entries: AuditEntry[];
}

function day(back: number): string {
  return new Date(Date.now() - back * 86_400_000).toISOString().slice(0, 10);
}

/** Who changed what, and when: keys, structure, limits, positions entered by hand, alert handling, and sign-ins. Admin only. */
@Component({
  selector: 'app-audit-page',
  imports: [DatePipe, FormsModule, NzAlertModule, NzButtonModule, NzSelectModule, NzTableModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page-head">
      <h1>Audit trail</h1>
      <span class="sub">Who changed what, and when. Entries are added by the system and cannot be edited.</span>
    </div>
    <div class="panel">
      <div class="bar">
        <label>
          Fund
          <nz-select [ngModel]="fundId()" (ngModelChange)="fundId.set($event)" nzSize="small" style="width:230px;margin-left:4px" data-testid="audit-fund">
            <nz-option [nzValue]="null" nzLabel="Everything, with sign-ins" />
            @for (fund of funds() ?? []; track fund.id) {
              <nz-option [nzValue]="fund.id" [nzLabel]="fund.name" />
            }
          </nz-select>
        </label>
        <label>From <input type="date" [ngModel]="from()" (ngModelChange)="from.set($event)" [max]="to()" /></label>
        <label>To <input type="date" [ngModel]="to()" (ngModelChange)="to.set($event)" [min]="from()" /></label>
        <span class="quick"><a (click)="last(7)">7 days</a><a (click)="last(30)">30 days</a><a (click)="last(365)">1 year</a></span>
        <span style="flex:1"></span>
        <a nz-button nzSize="small" [href]="csvUrl()" data-testid="audit-csv">Download CSV</a>
      </div>
      @if (loaded.failed()) {
        <nz-alert nzType="error" nzMessage="Could not load the audit trail." nzShowIcon />
      }
      @if (loaded.data(); as list) {
        @if (list.truncated) {
          <nz-alert nzType="info" nzShowIcon nzMessage="Only the newest 2,000 entries of this period are shown. Narrow the dates, or download the same 2,000 as a file." style="display:block;margin-bottom:10px" />
        }
        <nz-table [nzData]="list.entries" nzTemplateMode [nzShowPagination]="false" nzSize="small" data-testid="audit-table">
          <thead>
            <tr><th>When (UTC)</th><th>Who</th><th>What</th><th>Fund</th><th>To what</th><th>Sent</th></tr>
          </thead>
          <tbody>
            @for (e of list.entries; track e.id) {
              <tr>
                <td class="nowrap">{{ e.at | date: 'MM-dd-yyyy HH:mm:ss' : 'UTC' }}</td>
                <td>{{ e.userName }}</td>
                <td><b>{{ e.action }}</b></td>
                <td>{{ e.fundName ?? '–' }}</td>
                <td>{{ e.subject ?? '–' }}</td>
                <td class="detail">{{ e.detail ?? '' }}</td>
              </tr>
            }
            @if (list.entries.length === 0) {
              <tr><td colspan="6" style="text-align:center;color:var(--kft-text-3);padding:28px">Nothing was changed in this period.</td></tr>
            }
          </tbody>
        </nz-table>
        <p class="note">Secrets are never written here: a key is shown by its last four characters, a file by its size. The trail starts on the day it was switched on; earlier changes are not in it.</p>
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

    .detail {
      color: var(--kft-text-2);
      font-size: 12px;
      max-width: 380px;
      overflow-wrap: anywhere;
    }

    .note {
      color: var(--kft-text-3);
      font-size: 12px;
      margin: 10px 0 0;
    }
  `,
})
export class AuditPage {
  private readonly http = inject(HttpClient);
  protected readonly funds = inject(Store).selectSignal(ViewState.funds);
  protected readonly fundId = signal<number | null>(null);
  protected readonly from = signal(day(30));
  protected readonly to = signal(day(0));
  private readonly query = computed(() => `${this.fundId() === null ? '' : 'fundId=' + this.fundId() + '&'}from=${this.from()}&to=${this.to()}`);
  protected readonly csvUrl = computed(() => `/api/audit?${this.query()}&format=csv`);

  protected readonly loaded = keptData(combineLatest([toObservable(this.query)]), toObservable(signal(0)), ([query]) => this.http.get<AuditList>(`/api/audit?${query}`));

  protected last(days: number): void {
    this.to.set(day(0));
    this.from.set(day(days));
  }
}
