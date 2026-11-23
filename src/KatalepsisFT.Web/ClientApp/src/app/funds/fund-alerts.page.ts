import { BreachRegisterPanel } from '../alerts/breach-register';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { NzAlertModule } from 'ng-zorro-antd/alert';
import { NzTabsModule } from 'ng-zorro-antd/tabs';
import { AlertRules } from '../alerts/alert-rules';
import { AlertsInbox } from '../alerts/alerts-inbox';
import { pageData } from '../shared/page-data';
import { PageHead } from '../shared/page-head';
import { FundTabs } from './fund-tabs';
import { FundsApi } from './funds.api';

@Component({
  selector: 'app-fund-alerts-page',
  imports: [NzAlertModule, NzTabsModule, PageHead, FundTabs, AlertsInbox, AlertRules, BreachRegisterPanel],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (page.failed()) {
      <nz-alert nzType="error" nzMessage="Could not load the fund." nzShowIcon />
    }
    @if (page.data(); as d) {
      <app-page-head [crumbs]="[{ label: d.fund.name, link: ['/funds', d.fund.id] }, { label: 'Alerts' }]" [title]="d.fund.name" [renameUrl]="'/api/funds/' + d.fund.id + '/name'" renameWhat="the fund" [demo]="d.fund.isDemo" sub="Alerts, what was done about them, and the rules behind them" [asOf]="d.asOf" [showFilter]="false" />
      <app-fund-tabs [fundId]="d.fund.id" />
      <!-- Each tab is built again when it is opened, so the inbox shows what a rule change just raised or closed. -->
      <nz-tabs [nzSelectedIndex]="tab()" (nzSelectedIndexChange)="tab.set($event)">
        <nz-tab nzTitle="Inbox">
          @if (tab() === 0) {
            <app-alerts-inbox [fundId]="d.fund.id" />
          }
        </nz-tab>
        <nz-tab nzTitle="Rules">
          @if (tab() === 1) {
            <app-alert-rules [fundId]="d.fund.id" />
          }
        </nz-tab>
        <nz-tab nzTitle="Breach register">
          @if (tab() === 2) {
            <kft-breach-register [fundId]="d.fund.id" />
          }
        </nz-tab>
      </nz-tabs>
    }
  `,
})
export class FundAlertsPage {
  private readonly api = inject(FundsApi);

  protected readonly tab = signal(0);
  protected readonly page = pageData((params) => this.api.overview(Number(params.get('fundId')), 'All'));
}
