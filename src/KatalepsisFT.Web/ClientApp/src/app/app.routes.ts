import { Routes } from '@angular/router';
import { adminOnly, hasSession, signedIn, signedOut } from './core/guards';

export const routes: Routes = [
  { path: 'login', canActivate: [signedOut], loadComponent: () => import('./pages/login.page').then((m) => m.LoginPage) },
  {
    path: 'change-password',
    canActivate: [hasSession],
    loadComponent: () => import('./pages/change-password.page').then((m) => m.ChangePasswordPage),
  },
  {
    path: '',
    canActivate: [signedIn],
    loadComponent: () => import('./layout/shell').then((m) => m.Shell),
    children: [
      { path: '', pathMatch: 'full', loadComponent: () => import('./pages/home.page').then((m) => m.HomePage) },
      { path: 'funds/:fundId', loadComponent: () => import('./funds/fund-overview.page').then((m) => m.FundOverviewPage) },
      { path: 'funds/:fundId/holdings', loadComponent: () => import('./funds/fund-holdings.page').then((m) => m.FundHoldingsPage) },
      { path: 'funds/:fundId/performance', loadComponent: () => import('./funds/fund-performance.page').then((m) => m.FundPerformancePage) },
      { path: 'funds/:fundId/risk', loadComponent: () => import('./funds/fund-risk.page').then((m) => m.FundRiskPage) },
      { path: 'funds/:fundId/liquidity', loadComponent: () => import('./funds/fund-liquidity.page').then((m) => m.FundLiquidityPage) },
      { path: 'funds/:fundId/alerts', loadComponent: () => import('./funds/fund-alerts.page').then((m) => m.FundAlertsPage) },
      { path: 'funds/:fundId/connections', canActivate: [adminOnly], loadComponent: () => import('./funds/fund-connections.page').then((m) => m.FundConnectionsPage) },
      { path: 'funds/:fundId/assets/:symbol', loadComponent: () => import('./funds/asset.page').then((m) => m.AssetPage) },
      { path: 'sleeves/:sleeveId', loadComponent: () => import('./funds/sleeve.page').then((m) => m.SleevePage) },
      { path: 'managers/:managerId', loadComponent: () => import('./funds/manager.page').then((m) => m.ManagerPage) },
      { path: 'admin/funds/new', canActivate: [adminOnly], loadComponent: () => import('./pages/new-fund.page').then((m) => m.NewFundPage) },
      {
        path: 'admin/system',
        canActivate: [adminOnly],
        loadComponent: () => import('./pages/system-status.page').then((m) => m.SystemStatusPage),
      },
    ],
  },
  { path: '**', redirectTo: '' },
];
