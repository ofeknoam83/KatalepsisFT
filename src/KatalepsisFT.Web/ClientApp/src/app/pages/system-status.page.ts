import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NzAlertModule } from 'ng-zorro-antd/alert';
import { NzTableModule } from 'ng-zorro-antd/table';
import { NzTagModule } from 'ng-zorro-antd/tag';
import { catchError, of, switchMap, timer } from 'rxjs';

interface SystemStatus {
  postgres: string;
  keyValue: string;
  workerHeartbeatAgeSeconds: Record<string, number | null>;
  isReady: boolean;
}

interface Row {
  name: string;
  detail: string;
  state: 'ok' | 'late' | 'down';
}

/** A worker publishes every 10 seconds; after three missed beats it is shown as late. */
const LATE_AFTER_SECONDS = 30;

@Component({
  selector: 'app-system-status-page',
  imports: [NzAlertModule, NzTableModule, NzTagModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page-head"><h1>System status</h1><span class="sub">What this web instance can reach right now. Refreshes every 10 seconds.</span></div>

    @if (failed()) {
      <nz-alert nzType="error" nzMessage="Could not load the status. Retrying." nzShowIcon style="display:block;margin-bottom:16px" />
    }

    <nz-table #table [nzData]="rows()" [nzShowPagination]="false" [nzLoading]="rows().length === 0 && !failed()" nzSize="small" class="panel">
      <thead>
        <tr>
          <th>Component</th>
          <th>Detail</th>
          <th>State</th>
        </tr>
      </thead>
      <tbody>
        @for (row of table.data; track row.name) {
          <tr>
            <td>{{ row.name }}</td>
            <td>{{ row.detail }}</td>
            <td>
              @switch (row.state) {
                @case ('ok') {
                  <nz-tag nzColor="success">OK</nz-tag>
                }
                @case ('late') {
                  <nz-tag nzColor="warning">Late</nz-tag>
                }
                @default {
                  <nz-tag nzColor="error">Not answering</nz-tag>
                }
              }
            </td>
          </tr>
        }
      </tbody>
    </nz-table>
  `,
})
export class SystemStatusPage {
  private readonly http = inject(HttpClient);

  protected readonly rows = signal<Row[]>([]);
  protected readonly failed = signal(false);

  constructor() {
    timer(0, 10_000)
      .pipe(
        switchMap(() => this.http.get<SystemStatus>('/api/system/status').pipe(catchError(() => of(null)))),
        takeUntilDestroyed(),
      )
      .subscribe((status) => {
        this.failed.set(status === null);
        if (status !== null) {
          this.rows.set(toRows(status));
        }
      });
  }
}

function toRows(status: SystemStatus): Row[] {
  const store = (name: string, value: string): Row => ({ name, detail: value, state: value === 'ok' ? 'ok' : 'down' });
  const workers = Object.entries(status.workerHeartbeatAgeSeconds).map(([group, age]): Row => {
    const name = `Worker: ${group}`;
    if (age === null) {
      return { name, detail: 'No heartbeat received by this instance yet', state: 'down' };
    }

    return { name, detail: `Last heartbeat ${Math.round(age)} s ago`, state: age > LATE_AFTER_SECONDS ? 'late' : 'ok' };
  });
  return [store('Postgres', status.postgres), store('Key Value', status.keyValue), ...workers];
}
