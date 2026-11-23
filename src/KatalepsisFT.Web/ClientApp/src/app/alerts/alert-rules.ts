import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { keptData } from '../shared/page-data';
import { FormControl, FormGroup, FormsModule, ReactiveFormsModule, Validators } from '@angular/forms';
import { Store } from '@ngxs/store';
import { NzAlertModule } from 'ng-zorro-antd/alert';
import { NzButtonModule } from 'ng-zorro-antd/button';
import { NzFormModule } from 'ng-zorro-antd/form';
import { NzInputNumberModule } from 'ng-zorro-antd/input-number';
import { NzModalModule } from 'ng-zorro-antd/modal';
import { NzPopconfirmModule } from 'ng-zorro-antd/popconfirm';
import { NzSelectModule } from 'ng-zorro-antd/select';
import { NzSwitchModule } from 'ng-zorro-antd/switch';
import { NzTableModule } from 'ng-zorro-antd/table';
import { NzTagModule } from 'ng-zorro-antd/tag';
import { Observable } from 'rxjs';
import { SessionState } from '../core/session.state';
import { AlertsApi } from './alerts.api';
import { AlertMetric, AlertOperator, AlertRule, AlertSeverity, METRICS, SEVERITY_COLOR, SaveAlertRule } from './models';

interface RuleForm {
  scope: FormControl<string>;
  metric: FormControl<AlertMetric>;
  operator: FormControl<AlertOperator>;
  threshold: FormControl<number>;
  severity: FormControl<AlertSeverity>;
  isEnabled: FormControl<boolean>;
}

const DAYS: AlertMetric = 'SlowestDaysToLiquidate';

/** The conditions that raise alerts. Rules that follow a sleeve's mandate keep their condition; the rest are free to edit. */
@Component({
  selector: 'app-alert-rules',
  imports: [FormsModule, ReactiveFormsModule, NzAlertModule, NzButtonModule, NzFormModule, NzInputNumberModule, NzModalModule, NzPopconfirmModule, NzSelectModule, NzSwitchModule, NzTableModule, NzTagModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (error(); as message) {
      <nz-alert nzType="error" [nzMessage]="message" nzShowIcon nzCloseable (nzOnClose)="error.set(null)" style="display:block;margin-bottom:14px" />
    }
    <div class="panel">
      <h3>
        Rules
        <span class="hint">Checked after each daily close and whenever a rule changes</span>
        @if (isAdmin()) {
          <button nz-button nzType="primary" nzSize="small" style="margin-left:12px" (click)="open(null)" data-testid="new-rule">New rule</button>
        }
      </h3>
      <nz-table [nzData]="data()?.rules ?? []" nzTemplateMode [nzShowPagination]="false" nzSize="small" data-testid="rules-table">
        <thead><tr><th>Where</th><th>Condition</th><th>Severity</th><th>Source</th><th>State</th><th>On</th>@if (isAdmin()) {<th></th>}</tr></thead>
        <tbody>
          @for (rule of data()?.rules ?? []; track rule.id) {
            <tr>
              <td><b>{{ rule.scopeName }}</b> <span class="kind">{{ rule.scopeKind }}</span></td>
              <td>{{ rule.description }}</td>
              <td><nz-tag [nzColor]="severityColor[rule.severity]">{{ rule.severity }}</nz-tag></td>
              <td>{{ rule.fromMandate ? 'Mandate limit' : 'Custom' }}</td>
              <td>
                @if (!rule.isEnabled) {
                  <nz-tag>Off</nz-tag>
                } @else if (rule.hasActiveAlert) {
                  <nz-tag nzColor="red">Alert active</nz-tag>
                } @else {
                  <nz-tag nzColor="green">Within</nz-tag>
                }
              </td>
              <td><nz-switch nzSize="small" [nzDisabled]="!isAdmin()" [ngModel]="rule.isEnabled" [ngModelOptions]="{ standalone: true }" (ngModelChange)="save(api.updateRule(rule.id, { scopeKind: rule.scopeKind, scopeId: rule.scopeId, metric: rule.metric, operator: rule.operator, threshold: rule.threshold, severity: rule.severity, isEnabled: $event }))" /></td>
              @if (isAdmin()) {
                <td class="nowrap">
                  <a (click)="open(rule)">Edit</a>
                  @if (!rule.fromMandate) {
                    <a style="margin-left:12px;color:#cf1322" nz-popconfirm nzPopconfirmTitle="Delete this rule and the history of its alerts?" nzOkText="Delete" (nzOnConfirm)="save(api.deleteRule(rule.id))">Delete</a>
                  }
                </td>
              }
            </tr>
          }
        </tbody>
      </nz-table>
    </div>

    <nz-modal [nzVisible]="editing() !== undefined" [nzTitle]="editing() ? 'Edit rule' : 'New rule'" nzOkText="Save" [nzOkDisabled]="form.invalid" (nzOnOk)="submit()" (nzOnCancel)="editing.set(undefined)">
      <ng-container *nzModalContent>
        <form nz-form nzLayout="vertical" [formGroup]="form">
          @if (formError(); as message) {
            <nz-alert nzType="error" [nzMessage]="message" nzShowIcon style="display:block;margin-bottom:14px" data-testid="rule-error" />
          }
          @if (editing()?.fromMandate) {
            <nz-alert nzType="info" nzShowIcon nzMessage="This rule follows a mandate limit. Its condition changes with the limit; here you can set its severity and switch it on or off." style="display:block;margin-bottom:14px" />
          }
          <nz-form-item>
            <nz-form-label>Where</nz-form-label>
            <nz-select formControlName="scope" data-testid="rule-scope">
              @for (scope of data()?.scopes ?? []; track scope.kind + scope.id) {
                <nz-option [nzValue]="scope.kind + ':' + scope.id" [nzLabel]="scope.name + ' · ' + scope.kind" />
              }
            </nz-select>
          </nz-form-item>
          <nz-form-item>
            <nz-form-label>Figure</nz-form-label>
            <nz-select formControlName="metric" data-testid="rule-metric">
              @for (metric of metrics; track metric.value) {
                <nz-option [nzValue]="metric.value" [nzLabel]="metric.label" />
              }
            </nz-select>
          </nz-form-item>
          <div style="display:grid;grid-template-columns:1fr 1fr 1fr;gap:12px">
            <nz-form-item>
              <nz-form-label>Raise when it is</nz-form-label>
              <nz-select formControlName="operator">
                <nz-option nzValue="Above" nzLabel="Above" />
                <nz-option nzValue="Below" nzLabel="Below" />
              </nz-select>
            </nz-form-item>
            <nz-form-item>
              <nz-form-label>{{ unit() === 'days' ? 'Days' : 'Percent' }}</nz-form-label>
              <nz-input-number formControlName="threshold" [nzStep]="1" style="width:100%" data-testid="rule-threshold" />
            </nz-form-item>
            <nz-form-item>
              <nz-form-label>Severity</nz-form-label>
              <nz-select formControlName="severity">
                <nz-option nzValue="Critical" nzLabel="Critical" />
                <nz-option nzValue="Warning" nzLabel="Warning" />
                <nz-option nzValue="Info" nzLabel="Info" />
              </nz-select>
            </nz-form-item>
          </div>
          <label style="display:flex;gap:10px;align-items:center"><nz-switch formControlName="isEnabled" nzSize="small" /> Switched on</label>
          <p style="color:#8c8c8c;font-size:12px;margin:12px 0 0">Drawdowns and losses are negative: "below −25" raises when a manager is more than 25% under its peak.</p>
        </form>
      </ng-container>
    </nz-modal>
  `,
  styles: `
    .kind {
      margin-left: 6px;
      color: #8c8c8c;
      font-size: 11px;
      text-transform: uppercase;
      letter-spacing: 0.04em;
    }
  `,
})
export class AlertRules {
  protected readonly api = inject(AlertsApi);
  private readonly store = inject(Store);

  readonly fundId = input.required<number>();

  protected readonly metrics = METRICS;
  protected readonly severityColor = SEVERITY_COLOR;
  protected readonly isAdmin = this.store.selectSignal(SessionState.isAdmin);
  protected readonly error = signal<string | null>(null);
  /** What went wrong with the rule in the dialog; shown inside it, so the input is still there to correct. */
  protected readonly formError = signal<string | null>(null);

  /** undefined: closed; null: a new rule; otherwise the rule being edited. */
  protected readonly editing = signal<AlertRule | null | undefined>(undefined);
  private readonly version = signal(0);

  protected readonly data = keptData(
    toObservable(this.fundId),
    toObservable(this.version),
    (fundId) => this.api.rules(fundId),
    (fundId) => fundId,
  ).data;

  protected readonly form = new FormGroup<RuleForm>({
    scope: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    metric: new FormControl<AlertMetric>('GrossExposureOverNav', { nonNullable: true }),
    operator: new FormControl<AlertOperator>('Above', { nonNullable: true }),
    threshold: new FormControl(0, { nonNullable: true, validators: [Validators.required] }),
    severity: new FormControl<AlertSeverity>('Warning', { nonNullable: true }),
    isEnabled: new FormControl(true, { nonNullable: true }),
  });
  private readonly metric = toSignal(this.form.controls.metric.valueChanges, { initialValue: this.form.controls.metric.value });
  protected readonly unit = computed(() => (this.metric() === DAYS ? 'days' : 'percent'));

  protected open(rule: AlertRule | null): void {
    const first = this.data()?.scopes[0];
    this.form.enable();
    this.form.setValue({
      scope: rule ? `${rule.scopeKind}:${rule.scopeId}` : first ? `${first.kind}:${first.id}` : '',
      metric: rule?.metric ?? 'GrossExposureOverNav',
      operator: rule?.operator ?? 'Above',
      threshold: rule ? (rule.metric === DAYS ? rule.threshold : Math.round(rule.threshold * 1e6) / 1e4) : 100,
      severity: rule?.severity ?? 'Warning',
      isEnabled: rule?.isEnabled ?? true,
    });
    if (rule?.fromMandate) {
      for (const name of ['scope', 'metric', 'operator', 'threshold'] as const) {
        this.form.controls[name].disable();
      }
    }

    this.formError.set(null);
    this.editing.set(rule);
  }

  protected submit(): void {
    const value = this.form.getRawValue();
    const [scopeKind, scopeId] = value.scope.split(':');
    const body: SaveAlertRule = {
      scopeKind: scopeKind as SaveAlertRule['scopeKind'],
      scopeId: Number(scopeId),
      metric: value.metric,
      operator: value.operator,
      threshold: value.metric === DAYS ? value.threshold : value.threshold / 100,
      severity: value.severity,
      isEnabled: value.isEnabled,
    };
    const rule = this.editing();
    this.error.set(null);
    this.formError.set(null);
    (rule ? this.api.updateRule(rule.id, body) : this.api.createRule(this.fundId(), body)).subscribe({
      next: () => {
        this.editing.set(undefined);
        this.version.update((v) => v + 1);
      },
      // The dialog stays open with what was typed.
      error: (e: { error?: { message?: string } }) => this.formError.set(e.error?.message ?? 'Could not save the rule.'),
    });
  }

  /** Switches a rule on or off, or deletes it, straight from the table. */
  protected save(request: Observable<unknown>): void {
    this.error.set(null);
    request.subscribe({
      next: () => this.version.update((v) => v + 1),
      error: (e: { error?: { message?: string } }) => {
        this.error.set(e.error?.message ?? 'Could not save the rule.');
        // Reloaded so that a switch that was flipped goes back to what the server holds.
        this.version.update((v) => v + 1);
      },
    });
  }
}
