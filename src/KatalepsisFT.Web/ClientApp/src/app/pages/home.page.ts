import { ChangeDetectionStrategy, Component, effect, inject } from '@angular/core';
import { Router } from '@angular/router';
import { Store } from '@ngxs/store';
import { NzAlertModule } from 'ng-zorro-antd/alert';
import { NzButtonModule } from 'ng-zorro-antd/button';
import { NzEmptyModule } from 'ng-zorro-antd/empty';
import { NzSpinModule } from 'ng-zorro-antd/spin';
import { LoadFunds, ViewState } from '../funds/view.state';

/** The landing page: goes straight to the fund, real funds before the demo. */
@Component({
  selector: 'app-home-page',
  imports: [NzAlertModule, NzButtonModule, NzEmptyModule, NzSpinModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (funds() === null && failed()) {
      <nz-alert
        nzType="error"
        nzMessage="Could not load the funds."
        nzShowIcon
        [nzAction]="retry"
        data-testid="funds-failed"
      />
      <ng-template #retry
        ><button nz-button nzSize="small" (click)="load()">Try again</button></ng-template
      >
    } @else if (funds() === null) {
      <nz-spin nzSimple style="display:block;margin:80px auto" />
    } @else if (funds()?.length === 0) {
      <div style="background:#fff;border:1px solid var(--kft-line);border-radius:8px;padding:48px">
        <nz-empty nzNotFoundContent="No funds yet." />
      </div>
    }
  `,
})
export class HomePage {
  private readonly store = inject(Store);
  private readonly router = inject(Router);

  protected readonly funds = this.store.selectSignal(ViewState.funds);
  protected readonly failed = this.store.selectSignal(ViewState.fundsFailed);

  constructor() {
    this.load();
    effect(() => {
      const funds = this.funds();
      if (funds && funds.length > 0) {
        void this.router.navigate(['/funds', funds[0].id], { replaceUrl: true });
      }
    });
  }

  protected load(): void {
    this.store.dispatch(new LoadFunds());
  }
}
