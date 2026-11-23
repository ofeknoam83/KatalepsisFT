import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { Store } from '@ngxs/store';
import { NzAlertModule } from 'ng-zorro-antd/alert';
import { NzButtonModule } from 'ng-zorro-antd/button';
import { NzInputModule } from 'ng-zorro-antd/input';
import { NzInputNumberModule } from 'ng-zorro-antd/input-number';
import { NzPopconfirmModule } from 'ng-zorro-antd/popconfirm';
import { NzSelectModule } from 'ng-zorro-antd/select';
import { NzTagModule } from 'ng-zorro-antd/tag';
import { Observable, catchError, combineLatest, of, switchMap } from 'rxjs';
import { ConnectionsApi, LimitKind, MandateType, Structure } from '../connections/connections.api';
import { pageData } from '../shared/page-data';
import { PageHead } from '../shared/page-head';
import { FundTabs } from './fund-tabs';
import { FundsApi } from './funds.api';
import { LoadFunds } from './view.state';

interface LimitRow {
  kind: LimitKind;
  label: string;
  unit: 'percent' | 'days';
  hint: string;
}

/** Every limit a mandate can have. Shares are of the sleeve's own NAV. */
const LIMITS: LimitRow[] = [
  { kind: 'GrossExposureMax', label: 'Gross exposure, at most', unit: 'percent', hint: '200 allows gross exposure of twice NAV' },
  { kind: 'NetExposureMin', label: 'Net exposure, at least', unit: 'percent', hint: '−5 allows 5% net short' },
  { kind: 'NetExposureMax', label: 'Net exposure, at most', unit: 'percent', hint: '5 allows 5% net long' },
  { kind: 'SingleAssetMax', label: 'Largest single asset, at most', unit: 'percent', hint: 'gross exposure to one asset' },
  { kind: 'ManagerDrawdownMin', label: 'Manager drawdown, no deeper than', unit: 'percent', hint: 'negative: −25 is 25% below the peak' },
  { kind: 'DaysToLiquidateMax', label: 'Days to liquidate, at most', unit: 'days', hint: 'slowest asset at 10% of daily volume' },
];

interface ManagerDraft {
  id: number;
  name: string;
  capital: number | null;
  hasHistory: boolean;
  connections: number;
}

interface SleeveDraft {
  id: number;
  name: string;
  mandateType: MandateType;
  limits: Record<LimitKind, number | null>;
  managers: ManagerDraft[];
  newManager: string;
}

/** A fund's sleeves, each sleeve's mandate limits, and its managers. Admin only. */
@Component({
  selector: 'app-fund-structure-page',
  imports: [FormsModule, NzAlertModule, NzButtonModule, NzInputModule, NzInputNumberModule, NzPopconfirmModule, NzSelectModule, NzTagModule, PageHead, FundTabs],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styles: `
    .grid {
      display: grid;
      grid-template-columns: 1fr 1.25fr;
      gap: 28px;
    }
    .label {
      font-size: 12px;
      color: #8c8c8c;
      margin-bottom: 4px;
    }
    .limit {
      display: grid;
      grid-template-columns: 1fr 120px;
      gap: 10px;
      align-items: center;
      margin-bottom: 6px;
      font-size: 13px;
    }
    .limit small {
      display: block;
      color: #8c8c8c;
      font-size: 11px;
    }
    .manager {
      display: grid;
      grid-template-columns: 1fr 170px auto;
      gap: 8px;
      align-items: center;
      margin-bottom: 6px;
    }
    .row {
      display: flex;
      gap: 10px;
      align-items: center;
    }
  `,
  template: `
    @if (page.data(); as d) {
      <app-page-head [crumbs]="[{ label: d.fund.name, link: ['/funds', d.fund.id] }, { label: 'Structure' }]" [title]="d.fund.name" [demo]="d.fund.isDemo" sub="Sleeves, mandates and managers" [showFilter]="false" />
      <app-fund-tabs [fundId]="d.fund.id" />
      @if (error(); as message) {
        <nz-alert nzType="error" [nzMessage]="message" nzShowIcon nzCloseable (nzOnClose)="error.set(null)" style="display:block;margin-bottom:14px" />
      }
      @if (saved(); as message) {
        <nz-alert nzType="success" [nzMessage]="message" nzShowIcon nzCloseable (nzOnClose)="saved.set(null)" style="display:block;margin-bottom:14px" />
      }
      @if (data(); as s) {
        @if (!s.canEdit) {
          <nz-alert nzType="info" nzShowIcon nzMessage="This is the built-in example fund. Its structure is shown here but cannot be changed." style="display:block;margin-bottom:14px" />
        }
        <div class="panel" data-testid="fund-form">
          <h3>Fund</h3>
          <div class="row">
            <div style="flex:1;max-width:380px"><div class="label">Name</div><input nz-input [ngModel]="fundName()" (ngModelChange)="fundName.set($event)" [disabled]="!s.canEdit" maxlength="200" /></div>
            <div style="width:240px">
              <div class="label">Benchmark</div>
              <nz-select [ngModel]="benchmarkId()" (ngModelChange)="benchmarkId.set($event)" [nzDisabled]="!s.canEdit" nzAllowClear nzPlaceHolder="None" style="width:100%">
                @for (b of s.benchmarks; track b.id) {
                  <nz-option [nzValue]="b.id" [nzLabel]="b.name" />
                }
              </nz-select>
            </div>
            <button nz-button nzType="primary" style="margin-top:20px" [disabled]="!s.canEdit || fundName().trim() === ''" (click)="run(api.saveFund(s.id, fundName(), benchmarkId()), 'Fund saved.', true)">Save</button>
          </div>
        </div>

        @for (sleeve of drafts(); track sleeve.id) {
          <div class="panel" [attr.data-testid]="'sleeve-' + sleeve.name">
            <div class="row" style="margin-bottom:14px">
              <div style="flex:1;max-width:320px"><div class="label">Sleeve</div><input nz-input [(ngModel)]="sleeve.name" [disabled]="!s.canEdit" maxlength="200" /></div>
              <div style="width:190px">
                <div class="label">Mandate</div>
                <nz-select [(ngModel)]="sleeve.mandateType" [nzDisabled]="!s.canEdit" style="width:100%">
                  <nz-option nzValue="Beta" nzLabel="Beta" /><nz-option nzValue="MarketNeutral" nzLabel="Market Neutral" /><nz-option nzValue="Directional" nzLabel="Directional" /><nz-option nzValue="Other" nzLabel="Other" />
                </nz-select>
              </div>
              <span style="flex:1"></span>
              @if (s.canEdit) {
                <button nz-button nzType="primary" style="margin-top:20px" [disabled]="sleeve.name.trim() === ''" (click)="saveSleeve(sleeve)" data-testid="save-sleeve">Save sleeve and limits</button>
                <button nz-button nzDanger style="margin-top:20px" [disabled]="sleeve.managers.length > 0" nz-popconfirm nzPopconfirmTitle="Remove this sleeve?" nzOkText="Remove" (nzOnConfirm)="run(api.removeSleeve(sleeve.id), 'Sleeve removed.')">Remove</button>
              }
            </div>
            <div class="grid">
              <div>
                <div class="label">Mandate limits — leave a box empty for no limit</div>
                @for (limit of limits; track limit.kind) {
                  <div class="limit">
                    <span>{{ limit.label }}<small>{{ limit.hint }}</small></span>
                    <nz-input-number [(ngModel)]="sleeve.limits[limit.kind]" [nzDisabled]="!s.canEdit" [nzPlaceHolder]="limit.unit === 'days' ? 'days' : '%'" style="width:100%" [attr.data-testid]="'limit-' + limit.kind" />
                  </div>
                }
                <p style="color:#8c8c8c;font-size:12px;margin:8px 0 0">Each limit has a critical alert rule that follows it.</p>
              </div>
              <div>
                <div class="label">Managers — name and allocated capital in USD</div>
                @for (manager of sleeve.managers; track manager.id) {
                  <div class="manager">
                    <input nz-input [(ngModel)]="manager.name" [disabled]="!s.canEdit" maxlength="200" />
                    <nz-input-number [(ngModel)]="manager.capital" [nzMin]="0" [nzStep]="1000000" [nzDisabled]="!s.canEdit" style="width:100%" />
                    <span class="row">
                      @if (s.canEdit) {
                        <a (click)="run(api.saveManager(manager.id, manager.name, manager.capital ?? 0), 'Manager saved.')">Save</a>
                        @if (manager.hasHistory || manager.connections > 0) {
                          <nz-tag [title]="'A manager with history or venue keys stays in the fund'">{{ manager.connections > 0 ? manager.connections + (manager.connections === 1 ? ' key' : ' keys') : 'Has history' }}</nz-tag>
                        } @else {
                          <a style="color:#cf1322" nz-popconfirm nzPopconfirmTitle="Remove this manager?" nzOkText="Remove" (nzOnConfirm)="run(api.removeManager(manager.id), 'Manager removed.')">Remove</a>
                        }
                      }
                    </span>
                  </div>
                }
                @if (s.canEdit) {
                  <div class="manager" style="margin-top:10px">
                    <input nz-input [(ngModel)]="sleeve.newManager" placeholder="New manager" maxlength="200" data-testid="new-manager" />
                    <button nz-button [disabled]="sleeve.newManager.trim() === ''" (click)="run(api.addManager(sleeve.id, sleeve.newManager, 0), 'Manager added.')">Add a manager</button>
                  </div>
                }
              </div>
            </div>
          </div>
        }

        @if (s.canEdit) {
          <div class="panel">
            <h3>Add a sleeve</h3>
            <div class="row">
              <input nz-input [ngModel]="newSleeve()" (ngModelChange)="newSleeve.set($event)" placeholder="Sleeve name" maxlength="200" style="max-width:320px" data-testid="new-sleeve" />
              <nz-select [ngModel]="newSleeveType()" (ngModelChange)="newSleeveType.set($event)" style="width:190px">
                <nz-option nzValue="Beta" nzLabel="Beta" /><nz-option nzValue="MarketNeutral" nzLabel="Market Neutral" /><nz-option nzValue="Directional" nzLabel="Directional" /><nz-option nzValue="Other" nzLabel="Other" />
              </nz-select>
              <button nz-button [disabled]="newSleeve().trim() === ''" (click)="addSleeve(s.id)">Add</button>
            </div>
          </div>
        }
      }
    }
  `,
})
export class FundStructurePage {
  protected readonly api = inject(ConnectionsApi);
  private readonly funds = inject(FundsApi);
  private readonly store = inject(Store);

  protected readonly limits = LIMITS;
  protected readonly page = pageData((params) => this.funds.overview(Number(params.get('fundId')), 'All'));
  protected readonly error = signal<string | null>(null);
  protected readonly saved = signal<string | null>(null);
  protected readonly fundName = signal('');
  protected readonly benchmarkId = signal<number | null>(null);
  protected readonly newSleeve = signal('');
  protected readonly newSleeveType = signal<MandateType>('Directional');
  private readonly version = signal(0);
  private readonly fundId = computed(() => this.page.data()?.fund.id ?? null);

  protected readonly data = toSignal(
    combineLatest([toObservable(this.fundId), toObservable(this.version)]).pipe(switchMap(([fundId]) => (fundId === null ? of(null) : this.api.structure(fundId).pipe(catchError(() => of(null)))))),
    { initialValue: null },
  );

  /** An editable copy of what the server holds; rebuilt after every save. */
  protected readonly drafts = computed<SleeveDraft[]>(() => (this.data()?.sleeves ?? []).map((s) => this.draft(s)));

  constructor() {
    effect(() => {
      const s = this.data();
      if (s) {
        this.fundName.set(s.name);
        this.benchmarkId.set(s.benchmarkId);
      }
    });
  }

  private draft(sleeve: Structure['sleeves'][number]): SleeveDraft {
    const limits = Object.fromEntries(LIMITS.map((l) => [l.kind, null])) as Record<LimitKind, number | null>;
    for (const limit of sleeve.limits) {
      // Shares are stored as fractions and edited as percentages.
      limits[limit.kind] = limit.kind === 'DaysToLiquidateMax' ? limit.value : Math.round(limit.value * 1e6) / 1e4;
    }

    return {
      id: sleeve.id,
      name: sleeve.name,
      mandateType: sleeve.mandateType,
      limits,
      managers: sleeve.managers.map((m) => ({ id: m.id, name: m.name, capital: m.allocatedCapitalUsd, hasHistory: m.hasHistory, connections: m.connections })),
      newManager: '',
    };
  }

  protected saveSleeve(sleeve: SleeveDraft): void {
    const limits = LIMITS.filter((l) => sleeve.limits[l.kind] !== null && sleeve.limits[l.kind] !== undefined).map((l) => ({
      kind: l.kind,
      value: l.unit === 'days' ? sleeve.limits[l.kind]! : sleeve.limits[l.kind]! / 100,
    }));
    this.run(this.api.saveSleeve(sleeve.id, sleeve.name, sleeve.mandateType, limits), 'Sleeve and limits saved. The alert rules were checked against them.', true);
  }

  protected addSleeve(fundId: number): void {
    this.run(this.api.addSleeve(fundId, this.newSleeve(), this.newSleeveType()), 'Sleeve added.', true);
    this.newSleeve.set('');
  }

  /** Runs one change, then reloads the structure; names shown in the navigation are reloaded too when they may have changed. */
  protected run(request: Observable<unknown>, message: string, namesChanged = false): void {
    this.error.set(null);
    this.saved.set(null);
    request.subscribe({
      next: () => {
        this.saved.set(message);
        this.version.update((v) => v + 1);
        if (namesChanged) {
          this.store.dispatch(new LoadFunds());
          const id = this.fundId();
          if (id !== null) {
            this.funds.overview(id, 'All').subscribe();
          }
        }
      },
      error: (e: { error?: { message?: string } }) => {
        this.error.set(e.error?.message ?? 'That could not be saved.');
        this.version.update((v) => v + 1);
      },
    });
  }
}
