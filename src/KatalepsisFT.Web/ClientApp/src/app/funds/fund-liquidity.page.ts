import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { NzAlertModule } from 'ng-zorro-antd/alert';
import { LiquidityPanel } from '../risk/liquidity-panel';
import { pageData } from '../shared/page-data';
import { PageHead } from '../shared/page-head';
import { FundTabs } from './fund-tabs';
import { FundsApi } from './funds.api';

@Component({
  selector: 'app-fund-liquidity-page',
  imports: [NzAlertModule, PageHead, FundTabs, LiquidityPanel],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (page.failed()) {
      <nz-alert nzType="error" nzMessage="Could not load the fund." nzShowIcon />
    }
    @if (page.data(); as d) {
      <app-page-head [crumbs]="[{ label: d.fund.name, link: ['/funds', d.fund.id] }, { label: 'Liquidity' }]" [title]="d.fund.name" [demo]="d.fund.isDemo" sub="Time to turn positions into cash" [asOf]="d.asOf" [showFilter]="false" />
      <app-fund-tabs [fundId]="d.fund.id" />
      <app-liquidity-panel scope="Fund" [scopeId]="d.fund.id" />
    }
  `,
})
export class FundLiquidityPage {
  private readonly api = inject(FundsApi);

  protected readonly page = pageData((params) => this.api.overview(Number(params.get('fundId')), 'All'));
}
