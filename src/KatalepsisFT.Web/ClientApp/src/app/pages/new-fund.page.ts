import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormArray, FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { Store } from '@ngxs/store';
import { NzAlertModule } from 'ng-zorro-antd/alert';
import { NzButtonModule } from 'ng-zorro-antd/button';
import { NzFormModule } from 'ng-zorro-antd/form';
import { NzInputModule } from 'ng-zorro-antd/input';
import { NzInputNumberModule } from 'ng-zorro-antd/input-number';
import { NzSelectModule } from 'ng-zorro-antd/select';
import { ConnectionsApi, MandateType } from '../connections/connections.api';
import { LoadFunds } from '../funds/view.state';

type ManagerForm = FormGroup<{ name: FormControl<string>; allocatedCapitalUsd: FormControl<number> }>;
type SleeveForm = FormGroup<{ name: FormControl<string>; mandateType: FormControl<MandateType>; managers: FormArray<ManagerForm> }>;

/** Sets up a real fund: its sleeves and the managers in each. Venue keys are added afterwards on the fund's Connections tab. */
@Component({
  selector: 'app-new-fund-page',
  imports: [ReactiveFormsModule, NzAlertModule, NzButtonModule, NzFormModule, NzInputModule, NzInputNumberModule, NzSelectModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styles: `
    .sleeve {
      border: 1px solid #f0f0f0;
      border-radius: 8px;
      padding: 14px 16px;
      margin-bottom: 14px;
      background: #fafafa;
    }
    .row {
      display: grid;
      gap: 12px;
      align-items: center;
      margin-bottom: 8px;
    }
    .label {
      font-size: 12px;
      color: #8c8c8c;
      margin-bottom: 4px;
    }
  `,
  template: `
    <div class="page-head"><h1>New fund</h1><span class="sub">Sleeves and managers; positions arrive through venue keys you add afterwards</span></div>
    @if (error(); as message) {
      <nz-alert nzType="error" [nzMessage]="message" nzShowIcon style="display:block;margin-bottom:14px" />
    }
    <form class="panel" [formGroup]="form" (ngSubmit)="submit()" style="max-width:860px">
      <div class="label">Fund name</div>
      <input nz-input formControlName="name" maxlength="200" style="max-width:420px;margin-bottom:18px" data-testid="fund-name" />

      <div formArrayName="sleeves">
        @for (sleeve of form.controls.sleeves.controls; track sleeve; let s = $index) {
          <div class="sleeve" [formGroupName]="s">
            <div class="row" style="grid-template-columns:1fr 200px auto">
              <div><div class="label">Sleeve</div><input nz-input formControlName="name" maxlength="200" [attr.data-testid]="'sleeve-name-' + s" /></div>
              <div>
                <div class="label">Mandate</div>
                <nz-select formControlName="mandateType" style="width:100%">
                  <nz-option nzValue="Beta" nzLabel="Beta" /><nz-option nzValue="MarketNeutral" nzLabel="Market Neutral" /><nz-option nzValue="Directional" nzLabel="Directional" /><nz-option nzValue="Other" nzLabel="Other" />
                </nz-select>
              </div>
              <button nz-button nzType="text" nzDanger type="button" style="margin-top:20px" [disabled]="form.controls.sleeves.length === 1" (click)="form.controls.sleeves.removeAt(s)">Remove sleeve</button>
            </div>
            <div class="label">Managers</div>
            <div formArrayName="managers">
              @for (manager of sleeve.controls.managers.controls; track manager; let m = $index) {
                <div class="row" style="grid-template-columns:1fr 220px auto" [formGroupName]="m">
                  <input nz-input formControlName="name" placeholder="Manager name" maxlength="200" [attr.data-testid]="'manager-name-' + s + '-' + m" />
                  <nz-input-number formControlName="allocatedCapitalUsd" [nzMin]="0" [nzStep]="1000000" nzPlaceHolder="Allocated capital, USD" style="width:100%" />
                  <button nz-button nzType="text" type="button" [disabled]="sleeve.controls.managers.length === 1" (click)="sleeve.controls.managers.removeAt(m)">Remove</button>
                </div>
              }
            </div>
            <button nz-button nzSize="small" type="button" (click)="sleeve.controls.managers.push(manager())">Add a manager</button>
          </div>
        }
      </div>
      <div style="display:flex;gap:12px">
        <button nz-button type="button" (click)="form.controls.sleeves.push(sleeve())">Add a sleeve</button>
        <span style="flex:1"></span>
        <button nz-button nzType="primary" [disabled]="form.invalid" [nzLoading]="saving()" data-testid="create-fund">Create the fund</button>
      </div>
    </form>
  `,
})
export class NewFundPage {
  private readonly api = inject(ConnectionsApi);
  private readonly router = inject(Router);
  private readonly store = inject(Store);

  protected readonly error = signal<string | null>(null);
  protected readonly saving = signal(false);
  protected readonly form = new FormGroup({
    name: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    sleeves: new FormArray<SleeveForm>([this.sleeve()]),
  });

  protected manager(): ManagerForm {
    return new FormGroup({
      name: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
      // Required: an emptied box holds null, which the server cannot read as an amount.
      allocatedCapitalUsd: new FormControl(0, { nonNullable: true, validators: [Validators.required, Validators.min(0)] }),
    });
  }

  protected sleeve(): SleeveForm {
    return new FormGroup({
      name: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
      mandateType: new FormControl<MandateType>('Directional', { nonNullable: true }),
      managers: new FormArray<ManagerForm>([this.manager()]),
    });
  }

  protected submit(): void {
    if (this.form.invalid) {
      return;
    }

    this.saving.set(true);
    this.error.set(null);
    this.api.createFund({ ...this.form.getRawValue(), benchmarkId: null }).subscribe({
      next: (fund) => {
        this.store.dispatch(new LoadFunds());
        void this.router.navigate(['/funds', fund.id, 'connections']);
      },
      error: (e: { error?: { message?: string } }) => {
        this.saving.set(false);
        this.error.set(e.error?.message ?? 'The fund could not be created.');
      },
    });
  }
}
