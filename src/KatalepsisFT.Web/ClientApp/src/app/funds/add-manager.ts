import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { Store } from '@ngxs/store';
import { NzAlertModule } from 'ng-zorro-antd/alert';
import { NzButtonModule } from 'ng-zorro-antd/button';
import { NzFormModule } from 'ng-zorro-antd/form';
import { NzInputModule } from 'ng-zorro-antd/input';
import { NzInputNumberModule } from 'ng-zorro-antd/input-number';
import { NzModalModule } from 'ng-zorro-antd/modal';
import { NzRadioModule } from 'ng-zorro-antd/radio';
import { NzSelectModule } from 'ng-zorro-antd/select';
import { forkJoin } from 'rxjs';
import { Connections, ConnectionsApi, Venue } from '../connections/connections.api';
import { SessionState } from '../core/session.state';

type Source = 'key' | 'manual' | 'later';

interface ManagerForm {
  name: FormControl<string>;
  sleeveId: FormControl<number | null>;
  capital: FormControl<number | null>;
  source: FormControl<Source>;
  venue: FormControl<Venue | null>;
  label: FormControl<string>;
  apiKey: FormControl<string>;
  apiSecret: FormControl<string>;
  passphrase: FormControl<string>;
}

/**
 * The "Add a manager" button and its dialog: one step from nothing to a manager that is being read. It names the
 * manager, puts it in a sleeve with its capital, and then either connects an exchange key or wallet, or goes on to
 * the page where positions are entered by hand. Shown to administrators only.
 */
@Component({
  selector: 'kft-add-manager',
  imports: [ReactiveFormsModule, NzAlertModule, NzButtonModule, NzFormModule, NzInputModule, NzInputNumberModule, NzModalModule, NzRadioModule, NzSelectModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (isAdmin()) {
      <button nz-button nzSize="small" (click)="open()" data-testid="add-manager">Add a manager</button>
      <nz-modal
        [nzVisible]="visible()"
        nzTitle="Add a manager"
        [nzOkText]="okText()"
        [nzOkLoading]="saving()"
        [nzOkDisabled]="status() !== 'VALID' || incomplete()"
        [nzCancelText]="createdId() === null ? 'Cancel' : 'Finish later'"
        (nzOnOk)="submit()"
        (nzOnCancel)="close()"
      >
        <ng-container *nzModalContent>
          <form nz-form nzLayout="vertical" [formGroup]="form" autocomplete="off" data-testid="add-manager-form">
            @if (error(); as message) {
              <nz-alert nzType="error" [nzMessage]="message" nzShowIcon style="display:block;margin-bottom:14px" data-testid="add-manager-error" />
            }
            @if (createdId() !== null) {
              <nz-alert nzType="success" nzMessage="The manager has been added. Only its key or address is still missing." nzShowIcon style="display:block;margin-bottom:14px" />
            }
            <nz-form-item>
              <nz-form-label>Name</nz-form-label>
              <input nz-input formControlName="name" maxlength="200" placeholder="For example: Northgate Capital" data-testid="manager-name" />
            </nz-form-item>
            <div style="display:grid;grid-template-columns:1fr 1fr;gap:12px">
              <nz-form-item>
                <nz-form-label>Sleeve</nz-form-label>
                <nz-select formControlName="sleeveId" style="width:100%" data-testid="manager-sleeve">
                  @for (s of sleeves(); track s.id) {
                    <nz-option [nzValue]="s.id" [nzLabel]="s.name" />
                  }
                </nz-select>
              </nz-form-item>
              <nz-form-item>
                <nz-form-label>Allocated capital, USD</nz-form-label>
                <nz-input-number formControlName="capital" [nzMin]="0" [nzStep]="1000000" style="width:100%" data-testid="manager-capital" />
              </nz-form-item>
            </div>
            <nz-form-item>
              <nz-form-label>Where its positions come from</nz-form-label>
              <nz-radio-group formControlName="source" data-testid="manager-source">
                <label nz-radio nzValue="key" [nzDisabled]="!canAddKeys()">Exchange key or wallet</label>
                <label nz-radio nzValue="manual">Entered by hand or CSV</label>
                <label nz-radio nzValue="later">Decide later</label>
              </nz-radio-group>
            </nz-form-item>
            @if (!canAddKeys()) {
              <p class="note">Keys cannot be stored on this server yet: its master key is not configured.</p>
            }
            @switch (source()) {
              @case ('key') {
                <div style="display:grid;grid-template-columns:1fr 1fr;gap:12px">
                  <nz-form-item>
                    <nz-form-label>Venue</nz-form-label>
                    <nz-select formControlName="venue" nzShowSearch style="width:100%" data-testid="manager-venue">
                      @for (v of venues(); track v.venue) {
                        <nz-option [nzValue]="v.venue" [nzLabel]="v.name" />
                      }
                    </nz-select>
                  </nz-form-item>
                  <nz-form-item><nz-form-label>Label</nz-form-label><input nz-input formControlName="label" placeholder="Main account" maxlength="100" /></nz-form-item>
                </div>
                @if (usesAddress()) {
                  <nz-form-item><nz-form-label>Wallet address</nz-form-label><input nz-input formControlName="apiKey" autocomplete="off" placeholder="Paste the address" data-testid="manager-api-key" /></nz-form-item>
                  <p class="note">These accounts are public on their chain. Only the address is needed, and nothing can be traded or withdrawn through it.</p>
                } @else {
                  <nz-form-item><nz-form-label>API key (read-only)</nz-form-label><input nz-input formControlName="apiKey" autocomplete="off" data-testid="manager-api-key" /></nz-form-item>
                  <nz-form-item><nz-form-label>API secret</nz-form-label><input nz-input type="password" formControlName="apiSecret" autocomplete="new-password" data-testid="manager-api-secret" /></nz-form-item>
                  @if (needsPassphrase()) {
                    <nz-form-item><nz-form-label>Passphrase</nz-form-label><input nz-input type="password" formControlName="passphrase" autocomplete="new-password" /></nz-form-item>
                  }
                  <p class="note">The key is checked with the venue before it is saved, stored encrypted, and never shown again. A key that can trade or withdraw is refused.</p>
                }
              }
              @case ('manual') {
                <p class="note">After the manager is added you go straight to its page, where positions are typed in or uploaded as a CSV file.</p>
              }
              @case ('later') {
                <p class="note">The manager is added empty. A key, a wallet or positions can be given to it at any time; it counts in the fund from its first reading.</p>
              }
            }
            <p class="note">More keys and wallets can be added to the same manager afterwards, on its page, and a track record from before today can be imported there. It starts today. Whatever it holds at its first reading enters the fund as money arriving, not as a gain.</p>
          </form>
        </ng-container>
      </nz-modal>
    }
  `,
  styles: `
    :host {
      display: inline-block;
      font-weight: 400;
    }

    .note {
      color: var(--kft-text-3);
      font-size: 12px;
      margin: 0 0 8px;
    }
  `,
})
export class AddManager {
  readonly fundId = input.required<number>();
  /** The sleeve to offer first; the first of the fund when not given. */
  readonly sleeveId = input<number | null>(null);

  private readonly api = inject(ConnectionsApi);
  private readonly router = inject(Router);
  protected readonly isAdmin = inject(Store).selectSignal(SessionState.isAdmin);

  protected readonly visible = signal(false);
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  /** Set once the manager exists: a key that is then refused is tried again without adding the manager twice. */
  protected readonly createdId = signal<number | null>(null);
  protected readonly sleeves = signal<{ id: number; name: string }[]>([]);
  private readonly options = signal<Connections | null>(null);
  protected readonly venues = computed(() => this.options()?.venues ?? []);
  protected readonly canAddKeys = computed(() => this.options()?.canAddKeys ?? false);

  protected readonly form = new FormGroup<ManagerForm>({
    name: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.maxLength(200)] }),
    sleeveId: new FormControl<number | null>(null, { validators: [Validators.required] }),
    capital: new FormControl<number | null>(0, { validators: [Validators.required, Validators.min(0)] }),
    source: new FormControl<Source>('key', { nonNullable: true }),
    venue: new FormControl<Venue | null>(null),
    label: new FormControl('Main account', { nonNullable: true }),
    apiKey: new FormControl('', { nonNullable: true }),
    apiSecret: new FormControl('', { nonNullable: true }),
    passphrase: new FormControl('', { nonNullable: true }),
  });
  /**
   * Whether the form is valid, as a signal. The inputs live in the dialog's overlay, and typing there does not by
   * itself redraw this component; reading the form's own "invalid" in the template would leave the button stale.
   */
  protected readonly status = toSignal(this.form.statusChanges, { initialValue: this.form.status });
  protected readonly source = toSignal(this.form.controls.source.valueChanges, { initialValue: 'key' as Source });
  private readonly venue = toSignal(this.form.controls.venue.valueChanges, { initialValue: null });
  private readonly key = toSignal(this.form.controls.apiKey.valueChanges, { initialValue: '' });
  private readonly secret = toSignal(this.form.controls.apiSecret.valueChanges, { initialValue: '' });
  private readonly nameValue = toSignal(this.form.controls.name.valueChanges, { initialValue: '' });
  protected readonly usesAddress = computed(() => this.venues().find((v) => v.venue === this.venue())?.usesAddress ?? false);
  protected readonly needsPassphrase = computed(() => this.venues().find((v) => v.venue === this.venue())?.needsPassphrase ?? false);
  /** With a key chosen, the venue, the key and (unless it is a wallet) the secret must be there. */
  protected readonly incomplete = computed(
    () => this.nameValue().trim() === '' || (this.source() === 'key' && (this.venue() === null || this.key().trim() === '' || (!this.usesAddress() && this.secret().trim() === ''))),
  );
  protected readonly okText = computed(() => (this.source() === 'key' ? (this.createdId() === null ? 'Add and read the account' : 'Check the key again') : this.source() === 'manual' ? 'Add and enter positions' : 'Add the manager'));

  protected open(): void {
    this.error.set(null);
    this.createdId.set(null);
    this.form.enable();
    this.form.reset({ name: '', sleeveId: this.sleeveId(), capital: 0, source: 'key', venue: null, label: 'Main account', apiKey: '', apiSecret: '', passphrase: '' });
    this.visible.set(true);
    forkJoin([this.api.structure(this.fundId()), this.api.list(this.fundId())]).subscribe({
      next: ([structure, connections]) => {
        this.sleeves.set(structure.sleeves.map((s) => ({ id: s.id, name: s.name })));
        this.options.set(connections);
        const controls = this.form.controls;
        if (controls.sleeveId.value === null) {
          controls.sleeveId.setValue(structure.sleeves[0]?.id ?? null);
        }

        controls.venue.setValue(connections.venues[0]?.venue ?? null);
        if (!connections.canAddKeys) {
          controls.source.setValue('manual');
        }
      },
      error: () => this.error.set("The fund's sleeves could not be loaded. Close this and try again."),
    });
  }

  protected submit(): void {
    const value = this.form.getRawValue();
    this.error.set(null);
    this.saving.set(true);
    const existing = this.createdId();
    if (existing !== null) {
      this.connect(existing);
      return;
    }

    this.api.addManager(value.sleeveId!, value.name.trim(), value.capital ?? 0).subscribe({
      next: ({ id }) => {
        this.createdId.set(id);
        // The manager exists from here on; what is left to get right is only how it is read.
        this.form.controls.name.disable();
        this.form.controls.sleeveId.disable();
        this.form.controls.capital.disable();
        if (this.form.controls.source.value === 'key') {
          this.connect(id);
        } else {
          this.finish(id);
        }
      },
      error: (e: { error?: { message?: string } }) => {
        this.saving.set(false);
        this.error.set(e.error?.message ?? 'The manager could not be added.');
      },
    });
  }

  private connect(managerId: number): void {
    const value = this.form.getRawValue();
    if (value.source !== 'key') {
      this.finish(managerId);
      return;
    }

    const wallet = this.usesAddress();
    this.api.add({ managerId, venue: value.venue!, label: value.label.trim() || 'Main account', apiKey: value.apiKey, apiSecret: wallet ? null : value.apiSecret, passphrase: wallet ? null : value.passphrase || null }).subscribe({
      next: () => this.finish(managerId),
      error: (e: { error?: { message?: string } }) => {
        this.saving.set(false);
        this.error.set(e.error?.message ?? 'The key could not be saved.');
      },
    });
  }

  /** Whatever was chosen, the next thing to look at is the new manager's own page. */
  private finish(managerId: number): void {
    const manual = this.form.controls.source.value === 'manual';
    this.saving.set(false);
    this.visible.set(false);
    void this.router.navigate(['/managers', managerId], { queryParams: { tab: manual ? 'manual' : 'keys' } });
  }

  protected close(): void {
    const created = this.createdId();
    this.visible.set(false);
    if (created !== null) {
      // It was added without a key: show it, so it is clear that it exists and what it still needs.
      void this.router.navigate(['/managers', created], { queryParams: { tab: 'keys' } });
    }
  }
}
