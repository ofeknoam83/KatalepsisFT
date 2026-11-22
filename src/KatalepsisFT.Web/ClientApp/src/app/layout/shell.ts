import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { Store } from '@ngxs/store';
import { NzAvatarModule } from 'ng-zorro-antd/avatar';
import { NzDropDownModule } from 'ng-zorro-antd/dropdown';
import { NzLayoutModule } from 'ng-zorro-antd/layout';
import { NzMenuModule } from 'ng-zorro-antd/menu';
import { Logout, SessionState } from '../core/session.state';

@Component({
  selector: 'app-shell',
  imports: [RouterLink, RouterLinkActive, RouterOutlet, NzAvatarModule, NzDropDownModule, NzLayoutModule, NzMenuModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styles: `
    nz-layout.outer {
      min-height: 100vh;
    }
    nz-header {
      background: #fff;
      border-bottom: 1px solid #f0f0f0;
      padding: 0 20px;
      height: 56px;
      line-height: 56px;
      display: flex;
      align-items: center;
      justify-content: space-between;
    }
    nz-sider {
      background: #fff;
      border-right: 1px solid #f0f0f0;
    }
    .group {
      font-size: 11px;
      color: #8c8c8c;
      text-transform: uppercase;
      letter-spacing: 0.06em;
      padding: 14px 24px 4px;
    }
    .who {
      display: flex;
      align-items: center;
      gap: 10px;
      cursor: pointer;
    }
    nz-content {
      padding: 20px;
    }
  `,
  template: `
    <nz-layout class="outer">
      <nz-header>
        <a class="brand" routerLink="/"><span class="mark">K</span>Katalepsis <small>FT</small></a>
        <span class="who" nz-dropdown [nzDropdownMenu]="menu" nzTrigger="click" nzPlacement="bottomRight" data-testid="user-menu">
          <span>{{ session()?.displayName || session()?.email }}</span>
          <nz-avatar [nzText]="initials()" style="background:#e6f0ff;color:#1677ff" />
        </span>
        <nz-dropdown-menu #menu="nzDropdownMenu">
          <ul nz-menu>
            <li nz-menu-item nzDisabled>{{ session()?.email }} · {{ session()?.roles?.join(', ') }}</li>
            <li nz-menu-divider></li>
            <li nz-menu-item routerLink="/change-password">Change password</li>
            <li nz-menu-item (click)="signOut()">Sign out</li>
          </ul>
        </nz-dropdown-menu>
      </nz-header>
      <nz-layout>
        <nz-sider nzWidth="220px" nzTheme="light">
          <ul nz-menu nzMode="inline" style="border-inline-end:0;padding-top:8px">
            <li nz-menu-item routerLink="/" routerLinkActive="ant-menu-item-selected" [routerLinkActiveOptions]="{ exact: true }">Fund</li>
          </ul>
          @if (isAdmin()) {
            <div class="group">Admin</div>
            <ul nz-menu nzMode="inline" style="border-inline-end:0">
              <li nz-menu-item routerLink="/admin/system" routerLinkActive="ant-menu-item-selected">System status</li>
            </ul>
          }
        </nz-sider>
        <nz-content><router-outlet /></nz-content>
      </nz-layout>
    </nz-layout>
  `,
})
export class Shell {
  private readonly store = inject(Store);
  private readonly router = inject(Router);

  protected readonly session = this.store.selectSignal(SessionState.session);
  protected readonly isAdmin = this.store.selectSignal(SessionState.isAdmin);
  protected readonly initials = computed(() => {
    const name = this.session()?.displayName || this.session()?.email || '';
    return name
      .split(/[\s@.]+/)
      .filter((part) => part.length > 0)
      .slice(0, 2)
      .map((part) => part[0].toUpperCase())
      .join('');
  });

  protected signOut(): void {
    this.store.dispatch(new Logout()).subscribe(() => void this.router.navigate(['/login']));
  }
}
