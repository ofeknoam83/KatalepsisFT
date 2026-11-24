import { ChangeDetectionStrategy, Component, effect, inject, input } from '@angular/core';
import { FundsApi } from './funds.api';
import { Store } from '@ngxs/store';
import { SessionState } from '../core/session.state';
import { RouterLink, RouterLinkActive } from '@angular/router';

@Component({
  selector: 'app-fund-tabs',
  imports: [RouterLink, RouterLinkActive],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styles: `
    nav {
      display: flex;
      gap: 4px 22px;
      flex-wrap: wrap;
      border-bottom: 1px solid #e3e6eb;
      margin-bottom: 18px;
    }
    a {
      padding: 10px 0 11px;
      color: var(--kft-text-2);
      font-weight: 500;
      font-size: 13px;
      margin-bottom: -1px;
    }
    a.on {
      color: var(--kft-brand);
      border-bottom: 2px solid var(--kft-brand);
      font-weight: 600;
    }
  `,
  template: `
    <nav>
      <a [routerLink]="['/funds', fundId()]" routerLinkActive="on" [routerLinkActiveOptions]="{ exact: true }">Overview</a>
      <a [routerLink]="['/funds', fundId(), 'holdings']" routerLinkActive="on">Holdings</a>
      <a [routerLink]="['/funds', fundId(), 'performance']" routerLinkActive="on">Performance</a>
      <a [routerLink]="['/funds', fundId(), 'risk']" routerLinkActive="on">Risk</a>
      <a [routerLink]="['/funds', fundId(), 'liquidity']" routerLinkActive="on">Liquidity</a>
      <a [routerLink]="['/funds', fundId(), 'trading']" routerLinkActive="on">Trading</a>
      <a [routerLink]="['/funds', fundId(), 'alerts']" routerLinkActive="on">Alerts</a>
      <a [routerLink]="['/funds', fundId(), 'reports']" routerLinkActive="on">Reports</a>
      @if (isAdmin()) {
        <a [routerLink]="['/funds', fundId(), 'structure']" routerLinkActive="on">Structure</a>
        <a [routerLink]="['/funds', fundId(), 'connections']" routerLinkActive="on">Connections</a>
      }
    </nav>
  `,
})
export class FundTabs {
  readonly fundId = input.required<number>();
  protected readonly isAdmin = inject(Store).selectSignal(SessionState.isAdmin);

  constructor() {
    // Every page of a fund carries these tabs, so this is where the side menu learns which fund is open, whichever
    // page was opened first.
    const api = inject(FundsApi);
    effect(() => api.open(this.fundId()));
  }
}
