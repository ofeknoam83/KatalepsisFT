import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { NzAlertModule } from 'ng-zorro-antd/alert';
import { PerformancePanel } from '../performance/performance-panel';
import { pageData } from '../shared/page-data';
import { PageHead } from '../shared/page-head';
import { FundTabs } from './fund-tabs';
import { FundsApi } from './funds.api';

@Component({
  selector: 'app-fund-performance-page',
  imports: [NzAlertModule, PageHead, FundTabs, PerformancePanel],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (page.failed()) {
      <nz-alert nzType="error" nzMessage="Could not load the fund." nzShowIcon />
    }
    @if (page.data(); as d) {
      <app-page-head [crumbs]="[{ label: d.fund.name, link: ['/funds', d.fund.id] }, { label: 'Performance' }]" [title]="d.fund.name" [renameUrl]="'/api/funds/' + d.fund.id + '/name'" renameWhat="the fund" [demo]="d.fund.isDemo" sub="Performance and attribution" [asOf]="d.asOf" [showFilter]="false" />
      <app-fund-tabs [fundId]="d.fund.id" />
      <app-performance-panel scope="Fund" [scopeId]="d.fund.id" />
    }
  `,
})
export class FundPerformancePage {
  private readonly api = inject(FundsApi);

  protected readonly page = pageData((params) => this.api.overview(Number(params.get('fundId')), 'All'));
}
