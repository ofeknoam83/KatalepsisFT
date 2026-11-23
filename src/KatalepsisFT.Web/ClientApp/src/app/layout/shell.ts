import { ChangeDetectionStrategy, Component, computed, effect, inject } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { Store } from '@ngxs/store';
import { NzAvatarModule } from 'ng-zorro-antd/avatar';
import { NzDropDownModule } from 'ng-zorro-antd/dropdown';
import { NzLayoutModule } from 'ng-zorro-antd/layout';
import { NzMenuModule } from 'ng-zorro-antd/menu';
import { LiveService } from '../core/live.service';
import { Logout, SessionState } from '../core/session.state';
import { LoadFunds, ViewState } from '../funds/view.state';

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
    .live {
      display: flex;
      align-items: center;
      gap: 6px;
      margin-right: 22px;
      font-size: 12px;
      color: #8c8c8c;
    }
    .live i {
      width: 7px;
      height: 7px;
      border-radius: 50%;
      background: #bfbfbf;
    }
    .live.on i {
      background: #52c41a;
    }
    nz-content {
      padding: 20px;
    }

    /* Smaller laptops: the menu gives up some of its width to the figures. */
    @media (max-width: 1500px) {
      nz-sider {
        flex: 0 0 184px !important;
        max-width: 184px !important;
        min-width: 184px !important;
        width: 184px !important;
      }

      nz-content {
        padding: 14px;
      }

      nz-sider li[nz-menu-item] {
        padding-left: 14px !important;
        padding-right: 8px !important;
        font-size: 13px;
      }

      .group {
        padding: 12px 14px 4px;
      }
    }
  `,
  template: `
    <nz-layout class="outer">
      <nz-header>
        <a class="brand" routerLink="/"><span class="mark">K</span>Katalepsis <small>FT</small></a>
        <span style="flex:1"></span>
        <span class="live" [class.on]="live.connected()" [title]="live.connected() ? 'Figures update as they change' : 'No live connection: pages refresh every 15 seconds'" data-testid="live-state">
          <i></i>{{ live.connected() ? 'Live' : 'Refreshing every 15 s' }}
        </span>
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
          <div class="group">Funds</div>
          <ul nz-menu nzMode="inline" style="border-inline-end:0">
            @for (fund of funds() ?? []; track fund.id) {
              <li nz-menu-item [routerLink]="['/funds', fund.id]" [class.ant-menu-item-selected]="fund.id === fundId()" [title]="fund.name">{{ fund.name }}@if (fund.isDemo) {<span style="color:#8c8c8c;font-size:11px;margin-left:6px">DEMO</span>}</li>
            }
          </ul>
          @if (sleeves().length > 0) {
            <div class="group">Sleeves</div>
            <ul nz-menu nzMode="inline" style="border-inline-end:0">
              @for (sleeve of sleeves(); track sleeve.id) {
                <li nz-menu-item [routerLink]="['/sleeves', sleeve.id]" routerLinkActive="ant-menu-item-selected">{{ sleeve.name }}</li>
              }
            </ul>
          }
          @if (isAdmin()) {
            <div class="group">Admin</div>
            <ul nz-menu nzMode="inline" style="border-inline-end:0">
              <li nz-menu-item routerLink="/admin/funds/new" routerLinkActive="ant-menu-item-selected">New fund</li>
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
  protected readonly live = inject(LiveService);

  protected readonly session = this.store.selectSignal(SessionState.session);
  protected readonly isAdmin = this.store.selectSignal(SessionState.isAdmin);
  protected readonly fundId = this.store.selectSignal(ViewState.fundId);
  protected readonly funds = this.store.selectSignal(ViewState.funds);
  protected readonly sleeves = this.store.selectSignal(ViewState.sleeves);
  protected readonly initials = computed(() => {
    const name = this.session()?.displayName || this.session()?.email || '';
    return name
      .split(/[\s@.]+/)
      .filter((part) => part.length > 0)
      .slice(0, 2)
      .map((part) => part[0].toUpperCase())
      .join('');
  });

  constructor() {
    this.store.dispatch(new LoadFunds());
    // The live connection follows whichever fund is open.
    effect(() => {
      const fundId = this.fundId();
      if (fundId !== null) {
        this.live.watch(fundId);
      }
    });
  }

  protected signOut(): void {
    this.live.stop();
    this.store.dispatch(new Logout()).subscribe(() => void this.router.navigate(['/login']));
  }
}
