import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { NzAlertModule } from 'ng-zorro-antd/alert';
import { RiskPanel } from '../risk/risk-panel';
import { pageData } from '../shared/page-data';
import { PageHead } from '../shared/page-head';
import { FundTabs } from './fund-tabs';
import { FundsApi } from './funds.api';

@Component({
  selector: 'app-fund-risk-page',
  imports: [NzAlertModule, PageHead, FundTabs, RiskPanel],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (page.failed()) {
      <nz-alert nzType="error" nzMessage="Could not load the fund." nzShowIcon />
    }
    @if (page.data(); as d) {
      <app-page-head [crumbs]="[{ label: d.fund.name, link: ['/funds', d.fund.id] }, { label: 'Risk' }]" [title]="d.fund.name" [demo]="d.fund.isDemo" sub="Scenarios, limits and volatility" [asOf]="d.asOf" [showFilter]="false" />
      <app-fund-tabs [fundId]="d.fund.id" />
      <app-risk-panel scope="Fund" [scopeId]="d.fund.id" />
    }
  `,
})
export class FundRiskPage {
  private readonly api = inject(FundsApi);

  protected readonly page = pageData((params) => this.api.overview(Number(params.get('fundId')), 'All'));
}
