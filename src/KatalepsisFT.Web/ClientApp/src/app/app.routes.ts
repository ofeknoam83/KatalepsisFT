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
      {
        path: 'admin/system',
        canActivate: [adminOnly],
        loadComponent: () => import('./pages/system-status.page').then((m) => m.SystemStatusPage),
      },
    ],
  },
  { path: '**', redirectTo: '' },
];
