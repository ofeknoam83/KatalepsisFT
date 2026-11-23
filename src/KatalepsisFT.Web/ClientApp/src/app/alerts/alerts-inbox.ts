import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Store } from '@ngxs/store';
import { NzAlertModule } from 'ng-zorro-antd/alert';
import { NzButtonModule } from 'ng-zorro-antd/button';
import { NzDropDownModule } from 'ng-zorro-antd/dropdown';
import { NzInputModule } from 'ng-zorro-antd/input';
import { NzMenuModule } from 'ng-zorro-antd/menu';
import { NzSegmentedModule } from 'ng-zorro-antd/segmented';
import { NzSelectModule } from 'ng-zorro-antd/select';
import { NzSpinModule } from 'ng-zorro-antd/spin';
import { NzTableModule } from 'ng-zorro-antd/table';
import { NzTagModule } from 'ng-zorro-antd/tag';
import { LiveService } from '../core/live.service';
import { Observable, catchError, combineLatest, of, switchMap, tap } from 'rxjs';
import { SessionState } from '../core/session.state';
import { ViewState } from '../funds/view.state';
import { AlertsApi } from './alerts.api';
import { Alert, AlertAction, AlertListState, Assignee, SEVERITY_COLOR } from './models';

const ACTION_TEXT: Record<AlertAction['kind'], string> = {
  Raised: 'Raised',
  Acknowledged: 'Acknowledged',
  Assigned: 'Assignment changed',
  Commented: 'Comment',
  Snoozed: 'Snoozed',
  Resolved: 'Resolved',
  Recovered: 'Recovered',
};

/** The alerts of a fund: what is active, who has it, and everything that was done about it. */
@Component({
  selector: 'app-alerts-inbox',
  imports: [DatePipe, FormsModule, RouterLink, NzAlertModule, NzButtonModule, NzDropDownModule, NzInputModule, NzMenuModule, NzSegmentedModule, NzSelectModule, NzSpinModule, NzTableModule, NzTagModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div style="display:flex;align-items:center;gap:12px;margin-bottom:14px;flex-wrap:wrap">
      <nz-segmented [nzOptions]="scopeOptions()" [ngModel]="sleeveId() ?? 0" (nzValueChange)="sleeveId.set(+$event === 0 ? null : +$event)" data-testid="alert-scope" />
      <nz-segmented [nzOptions]="states" [ngModel]="state()" (nzValueChange)="state.set($any($event))" data-testid="alert-state" />
      <span style="flex:1"></span>
      @if (isAdmin()) {
        <button nz-button (click)="evaluate()" [nzLoading]="busy()" data-testid="evaluate">Check the rules now</button>
      }
    </div>
    @if (error(); as message) {
      <nz-alert nzType="error" [nzMessage]="message" nzShowIcon nzCloseable (nzOnClose)="error.set(null)" style="display:block;margin-bottom:14px" />
    }
    @if (failed()) {
      <nz-alert nzType="error" nzMessage="Could not load the alerts." nzShowIcon />
    } @else if (data() === null) {
      <nz-spin nzSimple style="display:block;margin:60px auto" />
    }
    @if (data(); as d) {
      <div class="tiles">
        <div class="tile"><div class="k">Active</div><div class="v" [class.down]="d.counts.active > 0">{{ d.counts.active }}</div><div class="d">{{ d.counts.critical }} critical</div></div>
        <div class="tile"><div class="k">Needing attention</div><div class="v">{{ d.counts.needingAttention }}</div><div class="d">Open, not acknowledged or snoozed</div></div>
        <div class="tile"><div class="k">Unassigned</div><div class="v">{{ d.counts.unassigned }}</div><div class="d">Active with no owner</div></div>
        <div class="tile"><div class="k">Snoozed</div><div class="v">{{ d.counts.snoozed }}</div><div class="d">Still breaching, set aside</div></div>
      </div>

      <div class="panel">
        <nz-table [nzData]="d.alerts" nzTemplateMode [nzShowPagination]="false" nzSize="small" data-testid="alerts-table">
          <thead>
            <tr><th nzWidth="36px"></th><th>Severity</th><th>Where</th><th>What</th><th>Status</th><th>Owner</th><th>Raised</th></tr>
          </thead>
          <tbody>
            @for (alert of d.alerts; track alert.id) {
              <tr (click)="toggle(alert.id)" style="cursor:pointer" [class.muted]="alert.status === 'Resolved' || alert.isSnoozed">
                <td [nzExpand]="expanded() === alert.id" (nzExpandChange)="toggle(alert.id)" (click)="$event.stopPropagation()"></td>
                <td><nz-tag [nzColor]="severityColor[alert.severity]">{{ alert.severity }}</nz-tag></td>
                <td class="nowrap">
                  <a [routerLink]="scopeLink(alert)" (click)="$event.stopPropagation()"><b>{{ alert.scopeName }}</b></a>
                  <span class="kind">{{ alert.scopeKind }}</span>
                </td>
                <td>{{ alert.description }}@if (alert.fromMandate) {<span class="kind">Mandate</span>}</td>
                <td class="nowrap">
                  @if (alert.isSnoozed) {
                    <nz-tag>Snoozed until {{ alert.snoozedUntil | date: 'd MMM' : 'UTC' }}</nz-tag>
                  } @else {
                    <nz-tag [nzColor]="alert.status === 'Open' ? 'red' : alert.status === 'Acknowledged' ? 'gold' : 'green'">{{ alert.status }}</nz-tag>
                  }
                </td>
                <td class="nowrap">{{ alert.assigneeName ?? '–' }}</td>
                <td class="nowrap">{{ alert.raisedAt | date: 'MM-dd-yyyy HH:mm' : 'UTC' }}</td>
              </tr>
              <tr [nzExpand]="expanded() === alert.id">
                <div class="detail">
                  <div class="history">
                    <div class="head">History</div>
                    @for (action of alert.actions; track $index) {
                      <div class="line">
                        <span class="when">{{ action.at | date: 'MM-dd-yyyy HH:mm' : 'UTC' }}</span>
                        <span><b>{{ actionText[action.kind] }}</b> by {{ action.userName ?? 'the system' }}@if (action.note) {<span> — {{ action.note }}</span>}</span>
                      </div>
                    }
                    <div class="line"><span class="when">Figures</span><span>From the close of {{ alert.dataDate | date: 'd MMM y' : 'UTC' }}, last checked {{ alert.lastSeenAt | date: 'MM-dd-yyyy HH:mm' : 'UTC' }} (UTC)</span></div>
                  </div>
                  @if (isAdmin()) {
                    <div class="actions" data-testid="alert-actions">
                      <div class="head">Actions</div>
                      <textarea nz-input rows="2" placeholder="Comment, or the reason for resolving" [ngModel]="note()" (ngModelChange)="note.set($event)" maxlength="1000"></textarea>
                      <div class="buttons">
                        @if (alert.status === 'Open') {
                          <button nz-button nzType="primary" nzSize="small" (click)="act(api.acknowledge(alert.id))">Acknowledge</button>
                        }
                        <button nz-button nzSize="small" [disabled]="note().trim() === ''" (click)="act(api.comment(alert.id, note()), true)">Add comment</button>
                        @if (alert.status !== 'Resolved') {
                          <button nz-button nzSize="small" nz-dropdown [nzDropdownMenu]="snooze" nzTrigger="click">Snooze</button>
                          <nz-dropdown-menu #snooze="nzDropdownMenu">
                            <ul nz-menu>
                              @for (days of snoozeDays; track days) {
                                <li nz-menu-item (click)="act(api.snooze(alert.id, days))">For {{ days }} {{ days === 1 ? 'day' : 'days' }}</li>
                              }
                            </ul>
                          </nz-dropdown-menu>
                          <button nz-button nzSize="small" nzDanger (click)="act(api.resolve(alert.id, note().trim() || null), true)">Resolve</button>
                        }
                      </div>
                      @if (alert.status !== 'Resolved') {
                        <div class="assign">
                          <span>Owner</span>
                          <nz-select nzSize="small" nzAllowClear nzPlaceHolder="Nobody" style="width:200px" [ngModel]="alert.assigneeUserId" (ngModelChange)="act(api.assign(alert.id, $event ?? null))">
                            @for (person of assignees(); track person.id) {
                              <nz-option [nzValue]="person.id" [nzLabel]="person.name" />
                            }
                          </nz-select>
                        </div>
                        <p class="hint">Resolving by hand closes the alert even though the condition still holds. It is raised again only after the figure has recovered and breached anew.</p>
                      }
                    </div>
                  }
                </div>
              </tr>
            }
            @if (d.alerts.length === 0) {
              <tr><td colspan="7" style="text-align:center;color:#8c8c8c;padding:28px">{{ state() === 'Active' ? 'No active alerts. Every rule is within its limit.' : 'Nothing here yet.' }}</td></tr>
            }
          </tbody>
        </nz-table>
      </div>
    }
  `,
  styles: `
    .kind {
      margin-left: 8px;
      color: #8c8c8c;
      font-size: 11px;
      text-transform: uppercase;
      letter-spacing: 0.04em;
    }
    tr.muted td {
      color: #8c8c8c;
    }
    .detail {
      display: grid;
      grid-template-columns: 1.2fr 1fr;
      gap: 28px;
      padding: 6px 8px 10px 36px;
    }
    .head {
      font-size: 12px;
      color: #8c8c8c;
      margin-bottom: 6px;
    }
    .line {
      display: grid;
      grid-template-columns: 130px 1fr;
      gap: 10px;
      font-size: 12.5px;
      padding: 3px 0;
    }
    .when {
      color: #8c8c8c;
      font-variant-numeric: tabular-nums;
    }
    .buttons {
      display: flex;
      gap: 8px;
      margin: 8px 0;
      flex-wrap: wrap;
    }
    .assign {
      display: flex;
      align-items: center;
      gap: 10px;
      font-size: 12.5px;
    }
    .hint {
      color: #8c8c8c;
      font-size: 12px;
      margin: 8px 0 0;
    }
  `,
})
export class AlertsInbox {
  protected readonly api = inject(AlertsApi);
  private readonly store = inject(Store);
  private readonly route = inject(ActivatedRoute);

  readonly fundId = input.required<number>();

  protected readonly severityColor = SEVERITY_COLOR;
  protected readonly actionText = ACTION_TEXT;
  protected readonly snoozeDays = [1, 3, 7];
  protected readonly states: { label: string; value: AlertListState }[] = [
    { label: 'Active', value: 'Active' },
    { label: 'Resolved', value: 'Resolved' },
    { label: 'All', value: 'All' },
  ];

  protected readonly isAdmin = this.store.selectSignal(SessionState.isAdmin);
  private readonly sleeves = this.store.selectSignal(ViewState.sleeves);
  protected readonly scopeOptions = computed(() => [{ label: 'Whole fund', value: 0 }, ...this.sleeves().map((s) => ({ label: s.name, value: s.id }))]);

  protected readonly sleeveId = signal<number | null>(Number(this.route.snapshot.queryParamMap.get('sleeve')) || null);
  protected readonly state = signal<AlertListState>('Active');
  protected readonly expanded = signal<number | null>(null);
  protected readonly note = signal('');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly failed = signal(false);
  private readonly version = signal(0);

  protected readonly data = toSignal(
    combineLatest([toObservable(this.fundId), toObservable(this.state), toObservable(this.sleeveId), toObservable(this.version), toObservable(inject(LiveService).tick)]).pipe(
      switchMap(([fundId, state, sleeveId]) =>
        this.api.list(fundId, state, sleeveId).pipe(
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
  protected readonly assignees = signal<Assignee[]>([]);

  constructor() {
    effect(() => {
      if (this.isAdmin()) {
        this.api
          .assignees()
          .pipe(catchError(() => of([])))
          .subscribe((people) => this.assignees.set(people));
      }
    });
  }

  protected toggle(id: number): void {
    this.expanded.set(this.expanded() === id ? null : id);
    this.note.set('');
  }

  protected scopeLink(alert: Alert): unknown[] {
    return alert.scopeKind === 'Fund' ? ['/funds', alert.scopeId] : [alert.scopeKind === 'Sleeve' ? '/sleeves' : '/managers', alert.scopeId];
  }

  /** Runs one workflow action and reloads the list, so counts and order follow. */
  protected act(request: Observable<unknown>, clearNote = false): void {
    this.error.set(null);
    request.subscribe({
      next: () => {
        if (clearNote) {
          this.note.set('');
        }

        this.version.update((v) => v + 1);
      },
      error: (e: { error?: { message?: string } }) => this.error.set(e.error?.message ?? 'That did not work. Try again.'),
    });
  }

  protected evaluate(): void {
    this.busy.set(true);
    this.api.evaluate(this.fundId()).subscribe({
      next: () => {
        this.busy.set(false);
        this.version.update((v) => v + 1);
      },
      error: () => {
        this.busy.set(false);
        this.error.set('Could not check the rules.');
      },
    });
  }
}
