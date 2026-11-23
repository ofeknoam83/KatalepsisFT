import { DatePipe, DecimalPipe } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { keptData } from '../shared/page-data';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { NzAlertModule } from 'ng-zorro-antd/alert';
import { NzButtonModule } from 'ng-zorro-antd/button';
import { NzFormModule } from 'ng-zorro-antd/form';
import { NzInputModule } from 'ng-zorro-antd/input';
import { NzInputNumberModule } from 'ng-zorro-antd/input-number';
import { NzModalModule } from 'ng-zorro-antd/modal';
import { NzPopconfirmModule } from 'ng-zorro-antd/popconfirm';
import { NzSelectModule } from 'ng-zorro-antd/select';
import { NzTableModule } from 'ng-zorro-antd/table';
import { NzTagModule } from 'ng-zorro-antd/tag';
import { Observable } from 'rxjs';
import { FORMAT_PIPES } from '../shared/format';

type InstrumentType = 'Spot' | 'Perp' | 'Future' | 'Option' | 'Equity' | 'Cash';

interface ManualPosition {
  id: number;
  type: InstrumentType;
  asset: string;
  quantity: number;
  price: number | null;
  entryPrice: number | null;
  expiry: string | null;
  strike: number | null;
  optionRight: 'Call' | 'Put' | null;
  delta: number | null;
  underlyingPrice: number | null;
  note: string | null;
  markPrice: number | null;
  priceFromFeed: boolean;
  updatedAt: string;
  updatedBy: string | null;
}

interface CashFlow {
  id: number;
  date: string;
  amountUsd: number;
  note: string | null;
  fromVenue: boolean;
}

interface ManualBook {
  managerId: number;
  manager: string;
  canEdit: boolean;
  positions: ManualPosition[];
  cashFlows: CashFlow[];
}

interface PositionForm {
  type: FormControl<InstrumentType>;
  asset: FormControl<string>;
  quantity: FormControl<number | null>;
  price: FormControl<number | null>;
  entryPrice: FormControl<number | null>;
  expiry: FormControl<string>;
  strike: FormControl<number | null>;
  optionRight: FormControl<'Call' | 'Put' | null>;
  delta: FormControl<number | null>;
  underlyingPrice: FormControl<number | null>;
  note: FormControl<string>;
}

const TEMPLATE =
  'type,asset,quantity,price,entry_price,expiry,strike,right,delta,underlying_price,note\n' +
  'Equity,AAPL,100,250,200,,,,,,Example: 100 shares valued at 250\n' +
  'Spot,BTC,2,,,,,,,,Example: no price given so the latest daily close is used\n' +
  'Perp,ETH,-10,3000,3100,,,,,,Example: a short is a negative quantity\n' +
  'Future,BTC,1,81000,80000,2026-12-25,,,,,\n' +
  'Option,BTC,-2,3200,4000,2026-12-25,90000,call,0.35,80000,Example: price is the premium per unit\n' +
  'Cash,USD,5000,,,,,,,,\n';

/** Positions and cash flows entered by hand for one manager: for equities, unsupported venues and anything no connection can read. */
@Component({
  selector: 'app-manual-book',
  imports: [DatePipe, DecimalPipe, ReactiveFormsModule, NzAlertModule, NzButtonModule, NzFormModule, NzInputModule, NzInputNumberModule, NzModalModule, NzPopconfirmModule, NzSelectModule, NzTableModule, NzTagModule, ...FORMAT_PIPES],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (errors().length > 0) {
      <nz-alert nzType="error" nzShowIcon nzCloseable (nzOnClose)="errors.set([])" [nzMessage]="errors().length === 1 ? errors()[0] : 'Nothing was changed. ' + errors().length + ' problems:'" [nzDescription]="errors().length > 1 ? list : null" style="display:block;margin-bottom:14px" data-testid="manual-errors" />
      <ng-template #list>
        <ul style="margin:0;padding-left:18px">
          @for (e of errors(); track $index) {
            <li>{{ e }}</li>
          }
        </ul>
      </ng-template>
    }
    @if (saved(); as message) {
      <nz-alert nzType="success" [nzMessage]="message" nzShowIcon nzCloseable (nzOnClose)="saved.set(null)" style="display:block;margin-bottom:14px" />
    }
    @if (book.failed()) {
      <nz-alert nzType="error" nzMessage="Could not load the positions entered by hand." nzShowIcon [nzAction]="retry" style="display:block;margin-bottom:14px" data-testid="manual-failed" />
      <ng-template #retry><button nz-button nzSize="small" (click)="reload()">Try again</button></ng-template>
    }
    @if (data(); as d) {
      @if (!d.canEdit) {
        <nz-alert nzType="info" nzShowIcon nzMessage="This manager belongs to the built-in example fund, whose positions cannot be changed." style="display:block;margin-bottom:14px" />
      }
      <div class="panel">
        <h3>
          Positions entered by hand
          <span class="hint">Added to whatever this manager's venue keys and wallets bring in</span>
          @if (d.canEdit) {
            <span style="margin-left:12px;display:inline-flex;gap:8px">
              <button nz-button nzType="primary" nzSize="small" (click)="open(null)" data-testid="add-position">Add a position</button>
              <button nz-button nzSize="small" (click)="file.click()" data-testid="upload">Upload a CSV file</button>
              <button nz-button nzSize="small" nzType="link" (click)="downloadTemplate()">Download a template</button>
            </span>
            <input #file type="file" accept=".csv,text/csv" hidden (change)="upload($event)" data-testid="upload-input" />
          }
        </h3>
        <nz-table [nzData]="d.positions" nzTemplateMode [nzShowPagination]="false" nzSize="small" data-testid="manual-table">
          <thead>
            <tr><th>Type</th><th>Asset</th><th class="num">Quantity</th><th class="num">Valued at</th><th class="num">Entry</th><th>Details</th><th>Note</th><th>Changed</th>@if (d.canEdit) {<th></th>}</tr>
          </thead>
          <tbody>
            @for (p of d.positions; track p.id) {
              <tr>
                <td>{{ p.type }}</td>
                <td><b>{{ p.asset }}</b></td>
                <td class="num" [class]="p.quantity | tone">{{ p.quantity | number: '1.0-8' }}</td>
                <td class="num nowrap">{{ p.markPrice | price }} @if (p.priceFromFeed) {<nz-tag>Daily close</nz-tag>}</td>
                <td class="num">{{ p.entryPrice | price }}</td>
                <td class="nowrap">{{ details(p) }}</td>
                <td>{{ p.note }}</td>
                <td class="nowrap" style="color:#8c8c8c">{{ p.updatedAt | date: 'MM-dd-yyyy HH:mm' : 'UTC' }}</td>
                @if (d.canEdit) {
                  <td class="nowrap">
                    <a (click)="open(p)">Edit</a>
                    <a style="margin-left:12px;color:#cf1322" nz-popconfirm nzPopconfirmTitle="Remove this position?" nzOkText="Remove" (nzOnConfirm)="run(http.delete('/api/manual-positions/' + p.id), 'Position removed.')">Remove</a>
                  </td>
                }
              </tr>
            }
            @if (d.positions.length === 0) {
              <tr><td colspan="9" style="text-align:center;color:#8c8c8c;padding:28px">No positions entered by hand.</td></tr>
            }
          </tbody>
        </nz-table>
        <p class="note">Uploading a file replaces every position in this table with the file's lines. If any line is wrong, nothing is changed and the faulty lines are listed. A position without a price is valued at the asset's latest daily close.</p>
      </div>

      <div class="panel">
        <h3>Cash flows <span class="hint">Money put into or taken out of this manager's book, so it is not counted as performance</span></h3>
        @if (d.canEdit) {
          <form [formGroup]="flowForm" (ngSubmit)="addFlow()" style="display:flex;gap:10px;align-items:center;margin-bottom:12px;flex-wrap:wrap">
            <input nz-input type="date" formControlName="date" style="width:160px" data-testid="flow-date" />
            <nz-input-number formControlName="amountUsd" nzPlaceHolder="Amount in USD (negative = out)" [nzStep]="10000" style="width:250px" data-testid="flow-amount" />
            <input nz-input formControlName="note" placeholder="Note" maxlength="500" style="width:260px" />
            <button nz-button [disabled]="flowForm.invalid" data-testid="add-flow">Add a cash flow</button>
          </form>
        }
        <nz-table [nzData]="d.cashFlows" nzTemplateMode [nzShowPagination]="false" nzSize="small" data-testid="flows-table">
          <thead><tr><th>Date</th><th class="num">Amount</th><th>Note</th><th>Source</th>@if (d.canEdit) {<th></th>}</tr></thead>
          <tbody>
            @for (f of d.cashFlows; track f.id) {
              <tr>
                <td>{{ f.date | date: 'MM-dd-yyyy' : 'UTC' }}</td>
                <td class="num" [class]="f.amountUsd | tone">{{ f.amountUsd | number: '1.2-2' }}</td>
                <td>{{ f.note }}</td>
                <td>{{ f.fromVenue ? 'Read from a venue' : 'Entered by hand' }}</td>
                @if (d.canEdit) {
                  <td>
                    @if (!f.fromVenue) {
                      <a style="color:#cf1322" nz-popconfirm nzPopconfirmTitle="Remove this cash flow?" nzOkText="Remove" (nzOnConfirm)="run(http.delete('/api/cash-flows/' + f.id), 'Cash flow removed.')">Remove</a>
                    }
                  </td>
                }
              </tr>
            }
            @if (d.cashFlows.length === 0) {
              <tr><td colspan="5" style="text-align:center;color:#8c8c8c;padding:20px">No cash flows.</td></tr>
            }
          </tbody>
        </nz-table>
      </div>

      <nz-modal [nzVisible]="editing() !== undefined" [nzTitle]="editing() ? 'Edit position' : 'Add a position'" nzOkText="Save" [nzOkDisabled]="form.invalid" (nzOnOk)="submit()" (nzOnCancel)="editing.set(undefined)">
        <ng-container *nzModalContent>
          <form nz-form nzLayout="vertical" [formGroup]="form">
            @if (formErrors().length > 0) {
              <nz-alert nzType="error" [nzMessage]="formErrors().join(' ')" nzShowIcon style="display:block;margin-bottom:14px" />
            }
            <div class="row3">
              <nz-form-item>
                <nz-form-label>Type</nz-form-label>
                <nz-select formControlName="type" data-testid="position-type">
                  @for (t of types; track t) {
                    <nz-option [nzValue]="t" [nzLabel]="t" />
                  }
                </nz-select>
              </nz-form-item>
              <nz-form-item><nz-form-label>Asset</nz-form-label><input nz-input formControlName="asset" placeholder="AAPL, BTC…" maxlength="32" data-testid="position-asset" /></nz-form-item>
              <nz-form-item><nz-form-label>Quantity (short is negative)</nz-form-label><nz-input-number formControlName="quantity" style="width:100%" data-testid="position-quantity" /></nz-form-item>
            </div>
            <div class="row3">
              <nz-form-item><nz-form-label>{{ type() === 'Option' ? 'Premium per unit, USD' : 'Price, USD' }}</nz-form-label><nz-input-number formControlName="price" [nzMin]="0" style="width:100%" [nzPlaceHolder]="type() === 'Option' ? '' : 'Empty = daily close'" data-testid="position-price" /></nz-form-item>
              <nz-form-item><nz-form-label>Entry price, USD</nz-form-label><nz-input-number formControlName="entryPrice" [nzMin]="0" style="width:100%" /></nz-form-item>
              @if (type() === 'Option' || type() === 'Future') {
                <nz-form-item><nz-form-label>Expiry</nz-form-label><input nz-input type="date" formControlName="expiry" /></nz-form-item>
              }
            </div>
            @if (type() === 'Option') {
              <div class="row3">
                <nz-form-item><nz-form-label>Strike</nz-form-label><nz-input-number formControlName="strike" [nzMin]="0" style="width:100%" /></nz-form-item>
                <nz-form-item>
                  <nz-form-label>Right</nz-form-label>
                  <nz-select formControlName="optionRight"><nz-option nzValue="Call" nzLabel="Call" /><nz-option nzValue="Put" nzLabel="Put" /></nz-select>
                </nz-form-item>
                <nz-form-item><nz-form-label>Delta (−1 to 1)</nz-form-label><nz-input-number formControlName="delta" [nzMin]="-1" [nzMax]="1" [nzStep]="0.05" style="width:100%" /></nz-form-item>
              </div>
              <nz-form-item><nz-form-label>Price of the underlying, USD</nz-form-label><nz-input-number formControlName="underlyingPrice" [nzMin]="0" style="width:200px" nzPlaceHolder="Empty = daily close" /></nz-form-item>
            }
            <nz-form-item><nz-form-label>Note</nz-form-label><input nz-input formControlName="note" maxlength="500" /></nz-form-item>
          </form>
        </ng-container>
      </nz-modal>
    }
  `,
  styles: `
    .note {
      color: #8c8c8c;
      font-size: 12px;
      margin: 10px 0 0;
    }
    .row3 {
      display: grid;
      grid-template-columns: 1fr 1fr 1fr;
      gap: 12px;
    }
  `,
})
export class ManualBookPanel {
  protected readonly http = inject(HttpClient);

  readonly managerId = input.required<number>();

  protected readonly types: InstrumentType[] = ['Equity', 'Spot', 'Perp', 'Future', 'Option', 'Cash'];
  protected readonly errors = signal<string[]>([]);
  protected readonly formErrors = signal<string[]>([]);
  protected readonly saved = signal<string | null>(null);

  /** undefined: closed; null: a new position; otherwise the one being edited. */
  protected readonly editing = signal<ManualPosition | null | undefined>(undefined);
  private readonly version = signal(0);

  protected readonly book = keptData(
    toObservable(this.managerId),
    toObservable(this.version),
    (id) => this.http.get<ManualBook>(`/api/managers/${id}/manual`),
    (id) => id,
  );
  protected readonly data = this.book.data;

  protected readonly form = new FormGroup<PositionForm>({
    type: new FormControl<InstrumentType>('Equity', { nonNullable: true }),
    asset: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    quantity: new FormControl<number | null>(null, { validators: [Validators.required] }),
    price: new FormControl<number | null>(null),
    entryPrice: new FormControl<number | null>(null),
    expiry: new FormControl('', { nonNullable: true }),
    strike: new FormControl<number | null>(null),
    optionRight: new FormControl<'Call' | 'Put' | null>(null),
    delta: new FormControl<number | null>(null),
    underlyingPrice: new FormControl<number | null>(null),
    note: new FormControl('', { nonNullable: true }),
  });
  protected readonly type = toSignal(this.form.controls.type.valueChanges, { initialValue: this.form.controls.type.value });

  protected readonly flowForm = new FormGroup({
    date: new FormControl(today(), { nonNullable: true, validators: [Validators.required] }),
    amountUsd: new FormControl<number | null>(null, { validators: [Validators.required] }),
    note: new FormControl('', { nonNullable: true }),
  });
  protected readonly hasPositions = computed(() => (this.data()?.positions.length ?? 0) > 0);

  protected details(p: ManualPosition): string {
    if (p.type === 'Option') {
      return `${p.expiry} · ${p.strike} ${p.optionRight?.toLowerCase()} · delta ${p.delta}`;
    }

    return p.type === 'Future' ? `expires ${p.expiry}` : '';
  }

  protected open(p: ManualPosition | null): void {
    this.formErrors.set([]);
    this.form.reset({
      type: p?.type ?? 'Equity',
      asset: p?.asset ?? '',
      quantity: p?.quantity ?? null,
      price: p?.price ?? null,
      entryPrice: p?.entryPrice ?? null,
      expiry: p?.expiry ?? '',
      strike: p?.strike ?? null,
      optionRight: p?.optionRight ?? null,
      delta: p?.delta ?? null,
      underlyingPrice: p?.underlyingPrice ?? null,
      note: p?.note ?? '',
    });
    this.editing.set(p);
  }

  protected submit(): void {
    const v = this.form.getRawValue();
    const body = { ...v, expiry: v.expiry || null, note: v.note || null };
    const current = this.editing();
    const request = current ? this.http.put(`/api/manual-positions/${current.id}`, body) : this.http.post(`/api/managers/${this.managerId()}/manual`, body);
    this.formErrors.set([]);
    request.subscribe({
      next: () => {
        this.editing.set(undefined);
        this.saved.set('Position saved. The fund has been revalued.');
        this.version.update((n) => n + 1);
      },
      error: (e: { error?: { errors?: string[]; message?: string } }) => this.formErrors.set(e.error?.errors ?? [e.error?.message ?? 'The position could not be saved.']),
    });
  }

  protected upload(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    // Cleared so that choosing the same file again, after fixing it, is noticed.
    input.value = '';
    if (!file) {
      return;
    }

    void file.text().then((csv) => this.run(this.http.post(`/api/managers/${this.managerId()}/manual/upload`, { csv }), `${file.name} loaded. It replaced the positions entered by hand.`));
  }

  protected downloadTemplate(): void {
    const link = document.createElement('a');
    link.href = URL.createObjectURL(new Blob([TEMPLATE], { type: 'text/csv' }));
    link.download = 'positions-template.csv';
    link.click();
    URL.revokeObjectURL(link.href);
  }

  protected addFlow(): void {
    const v = this.flowForm.getRawValue();
    // The amount and note are cleared only once the server has taken them; a refused cash flow stays to be corrected.
    this.run(this.http.post(`/api/managers/${this.managerId()}/cash-flows`, { date: v.date, amountUsd: v.amountUsd, note: v.note || null }), 'Cash flow added.', () => this.flowForm.patchValue({ amountUsd: null, note: '' }));
  }

  protected reload(): void {
    this.version.update((n) => n + 1);
  }

  protected run(request: Observable<unknown>, message: string, done?: () => void): void {
    this.errors.set([]);
    this.saved.set(null);
    request.subscribe({
      next: () => {
        done?.();
        this.saved.set(message);
        this.version.update((n) => n + 1);
      },
      error: (e: { error?: { errors?: string[]; message?: string } }) => this.errors.set(e.error?.errors ?? [e.error?.message ?? 'That did not work. Try again.']),
    });
  }
}

/** Today's date where the user is, as the date box wants it. toISOString would give the UTC day, which is a day off late in the evening west of UTC and early in the morning east of it. */
function today(): string {
  const now = new Date();
  return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`;
}
