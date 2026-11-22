import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';

@Component({
  selector: 'app-fund-tabs',
  imports: [RouterLink, RouterLinkActive],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styles: `
    nav {
      display: flex;
      gap: 26px;
      border-bottom: 1px solid #d9d9d9;
      margin-bottom: 14px;
    }
    a {
      padding: 8px 0 9px;
      color: #595959;
      font-size: 14px;
      margin-bottom: -1px;
    }
    a.on {
      color: #1677ff;
      border-bottom: 2px solid #1677ff;
      font-weight: 500;
    }
  `,
  template: `
    <nav>
      <a [routerLink]="['/funds', fundId()]" routerLinkActive="on" [routerLinkActiveOptions]="{ exact: true }">Overview</a>
      <a [routerLink]="['/funds', fundId(), 'holdings']" routerLinkActive="on">Holdings</a>
      <a [routerLink]="['/funds', fundId(), 'performance']" routerLinkActive="on">Performance</a>
    </nav>
  `,
})
export class FundTabs {
  readonly fundId = input.required<number>();
}
