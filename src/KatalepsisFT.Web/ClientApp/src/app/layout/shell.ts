import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { Store } from '@ngxs/store';
import { NzDropDownModule } from 'ng-zorro-antd/dropdown';
import { NzMenuModule } from 'ng-zorro-antd/menu';
import { LiveService } from '../core/live.service';
import { Logout, SessionState } from '../core/session.state';
import { SLEEVE_COLORS } from '../funds/models';
import { LoadFunds, ViewState } from '../funds/view.state';

/** The frame around every page: the side menu with the fund switcher, the open fund's sleeves, admin links and the user. */
@Component({
  selector: 'app-shell',
  imports: [RouterLink, RouterLinkActive, RouterOutlet, NzDropDownModule, NzMenuModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styles: `
    :host {
      display: flex;
      min-height: 100vh;
    }
    nav {
      flex: 0 0 256px;
      width: 256px;
      box-sizing: border-box;
      position: sticky;
      top: 0;
      height: 100vh;
      overflow-y: auto;
      background: var(--kft-card);
      border-right: 1px solid var(--kft-line);
      padding: 18px 14px 12px;
      display: flex;
      flex-direction: column;
      gap: 18px;
    }
    main {
      flex: 1 1 0;
      min-width: 0;
      padding: 22px 32px 40px;
    }
    .brand {
      padding: 2px 6px;
    }
    .switch {
      display: flex;
      align-items: center;
      gap: 10px;
      width: 100%;
      min-height: 52px;
      padding: 8px 10px;
      border: 1px solid #e1e4ea;
      border-radius: 10px;
      background: #fafbfc;
      font: inherit;
      color: inherit;
      text-align: left;
      cursor: pointer;
    }
    .switch:hover {
      border-color: #c9ceda;
    }
    .badge {
      flex: 0 0 30px;
      height: 30px;
      border-radius: 8px;
      background: #ececfb;
      color: var(--kft-brand);
      display: grid;
      place-items: center;
      font-weight: 600;
      font-size: 12px;
    }
    .switch .names {
      flex: 1 1 0;
      min-width: 0;
    }
    .switch b,
    .who b {
      display: block;
      font-weight: 600;
      font-size: 13.5px;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }
    .switch small,
    .who small {
      display: block;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
      font-size: 12px;
      color: var(--kft-text-3);
    }
    .group {
      font-size: 11px;
      font-weight: 600;
      letter-spacing: 0.06em;
      text-transform: uppercase;
      color: var(--kft-text-3);
      padding: 0 10px 6px;
    }
    .links {
      display: flex;
      flex-direction: column;
      gap: 2px;
    }
    .links a {
      display: flex;
      align-items: center;
      gap: 10px;
      min-height: 38px;
      padding: 0 10px;
      border-radius: 8px;
      color: #344054;
      font-weight: 500;
    }
    .links a:hover {
      background: #f5f6f8;
    }
    .links a.on {
      background: var(--kft-brand-soft);
      color: var(--kft-brand);
      font-weight: 600;
    }
    .links a.sleeve {
      padding-left: 14px;
    }
    .links a i {
      flex: 0 0 8px;
      height: 8px;
      border-radius: 3px;
    }
    .links svg {
      flex: 0 0 18px;
      color: var(--kft-text-3);
    }
    .links a.on svg {
      color: var(--kft-brand);
    }
    .foot {
      margin-top: auto;
      display: flex;
      flex-direction: column;
      gap: 8px;
      padding-top: 10px;
      border-top: 1px solid #eef0f3;
    }
    .live {
      display: inline-flex;
      align-items: center;
      gap: 7px;
      align-self: flex-start;
      margin-left: 6px;
      font-size: 12.5px;
      font-weight: 500;
      color: var(--kft-text-2);
      background: var(--kft-fill);
      padding: 4px 11px;
      border-radius: 999px;
    }
    .live i {
      width: 7px;
      height: 7px;
      border-radius: 50%;
      background: #98a2b3;
    }
    .live.on {
      color: #05603a;
      background: var(--kft-up-soft);
    }
    .live.on i {
      background: #12925a;
    }
    .who {
      display: flex;
      align-items: center;
      gap: 10px;
      padding: 6px;
      border-radius: 10px;
      cursor: pointer;
    }
    .who:hover {
      background: #f5f6f8;
    }
    .who .badge {
      border-radius: 50%;
      flex-basis: 32px;
      height: 32px;
      font-size: 13px;
    }
    .who span {
      min-width: 0;
    }

    /* The list that opens under the fund switcher. */
    .funds {
      width: 300px;
      background: #fff;
      border-radius: 14px;
      box-shadow: 0 12px 32px rgba(16, 24, 40, 0.16);
      border: 1px solid var(--kft-line);
      padding: 10px;
    }
    .funds input {
      width: 100%;
      box-sizing: border-box;
      height: 36px;
      border: 1px solid #e1e4ea;
      border-radius: 9px;
      padding: 0 10px;
      font: inherit;
      outline: none;
    }
    .funds input:focus {
      border-color: var(--kft-brand);
    }
    .funds ul {
      list-style: none;
      margin: 8px 0 0;
      padding: 0;
      max-height: 340px;
      overflow-y: auto;
    }
    .funds li a {
      display: flex;
      align-items: center;
      gap: 8px;
      min-height: 36px;
      padding: 0 10px;
      border-radius: 8px;
      color: var(--kft-text);
    }
    .funds li a:hover,
    .funds li a.on {
      background: var(--kft-brand-soft);
    }
    .funds li em {
      margin-left: auto;
      font-style: normal;
      font-size: 11px;
      font-weight: 600;
      color: var(--kft-text-3);
    }
    .funds .none {
      padding: 10px;
      color: var(--kft-text-3);
      font-size: 13px;
    }

    .toggle {
      display: none;
    }

    /* Smaller laptops: the menu gives up some of its width to the figures. */
    @media (max-width: 1500px) {
      nav {
        flex-basis: 216px;
        width: 216px;
        padding: 14px 10px 10px;
      }

      main {
        padding: 16px 18px 32px;
      }
    }

    /* Tablets and phones: the menu becomes a bar across the top; the button at its right opens the rest. */
    @media (max-width: 900px) {
      :host {
        flex-direction: column;
      }

      nav {
        flex: 0 0 auto;
        width: 100%;
        height: auto;
        position: static;
        border-right: 0;
        border-bottom: 1px solid var(--kft-line);
        flex-direction: row;
        flex-wrap: wrap;
        align-items: center;
        gap: 10px;
      }

      .brand {
        flex: 1 1 auto;
      }

      .toggle {
        display: grid;
        place-items: center;
        width: 44px;
        height: 44px;
        border: 1px solid #e1e4ea;
        border-radius: 10px;
        background: #fff;
        color: var(--kft-text);
        cursor: pointer;
      }

      .switch,
      .links,
      .foot {
        flex: 1 1 100%;
      }

      nav:not(.open) .links,
      nav:not(.open) .foot {
        display: none;
      }

      .foot {
        margin-top: 0;
      }

      main {
        padding: 14px 12px 28px;
      }
    }
  `,
  template: `
    <nav aria-label="Main" [class.open]="menuOpen()" (click)="closeOnLink($event)">
      <a class="brand" routerLink="/"><span class="mark">K</span>Katalepsis <small>FT</small></a>
      <button type="button" class="toggle" [attr.aria-expanded]="menuOpen()" aria-label="Menu" (click)="menuOpen.set(!menuOpen())">
        <svg aria-hidden="true" width="20" height="20" viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><path d="M3 6h14M3 10h14M3 14h14" /></svg>
      </button>

      <button type="button" class="switch" nz-dropdown [nzDropdownMenu]="fundMenu" nzTrigger="click" [(nzVisible)]="switching" data-testid="fund-switcher">
        <span class="badge" aria-hidden="true">{{ fundInitials() }}</span>
        <span class="names">
          <b>{{ current()?.name ?? 'Choose a fund' }}</b>
          <small>{{ currentKind() }}{{ otherCount() > 0 ? ' · ' + otherCount() + ' more' : '' }}</small>
        </span>
        <svg aria-hidden="true" width="16" height="16" viewBox="0 0 16 16" fill="none" stroke="#5b6474" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="M5 6l3-3 3 3M5 10l3 3 3-3" /></svg>
      </button>
      <nz-dropdown-menu #fundMenu="nzDropdownMenu">
        <div class="funds">
          <input type="search" placeholder="Find a fund or account" aria-label="Find a fund or account" [value]="search()" (input)="search.set($any($event.target).value)" />
          <ul>
            @for (fund of shownFunds(); track fund.id) {
              <li>
                <a [routerLink]="['/funds', fund.id]" [class.on]="fund.id === fundId()" (click)="switching = false; search.set('')" [title]="fund.name">
                  {{ fund.name }}
                  @if (fund.isDemo) {
                    <em>DEMO</em>
                  }
                  @if (fund.kind === 'Sma') {
                    <em [title]="'Separately managed account of ' + fund.ownerName">SMA</em>
                  }
                </a>
              </li>
            } @empty {
              <li class="none">Nothing matches.</li>
            }
          </ul>
        </div>
      </nz-dropdown-menu>

      @if (fundId(); as id) {
        <div class="links">
          <div class="group">This fund</div>
          <a [routerLink]="['/funds', id]" routerLinkActive="on">
            <svg aria-hidden="true" width="18" height="18" viewBox="0 0 18 18" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linejoin="round"><rect x="2.5" y="2.5" width="5.5" height="5.5" rx="1.2" /><rect x="10" y="2.5" width="5.5" height="5.5" rx="1.2" /><rect x="2.5" y="10" width="5.5" height="5.5" rx="1.2" /><rect x="10" y="10" width="5.5" height="5.5" rx="1.2" /></svg>
            Fund
          </a>
          @for (sleeve of sleeves(); track sleeve.id; let i = $index) {
            <a class="sleeve" [routerLink]="['/sleeves', sleeve.id]" routerLinkActive="on"><i [style.background]="colors[i % colors.length]"></i>{{ sleeve.name }}</a>
          }
        </div>
      }

      @if (isAdmin()) {
        <div class="links">
          <div class="group">Admin</div>
          <a routerLink="/admin/funds/new" routerLinkActive="on">
            <svg aria-hidden="true" width="18" height="18" viewBox="0 0 18 18" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"><circle cx="9" cy="9" r="6.5" /><path d="M9 6v6M6 9h6" /></svg>
            New fund or SMA
          </a>
          <a routerLink="/admin/users" routerLinkActive="on">
            <svg aria-hidden="true" width="18" height="18" viewBox="0 0 18 18" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"><circle cx="9" cy="6.5" r="3" /><path d="M3.5 15c.8-2.6 2.9-4 5.5-4s4.7 1.4 5.5 4" /></svg>
            Users
          </a>
          <a routerLink="/admin/strategy-keys" routerLinkActive="on">
            <svg aria-hidden="true" width="18" height="18" viewBox="0 0 18 18" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><circle cx="6" cy="12" r="3" /><path d="M8.2 9.8L15 3M12.5 5.5l2 2" /></svg>
            Strategy keys
          </a>
          <a routerLink="/admin/audit" routerLinkActive="on">
            <svg aria-hidden="true" width="18" height="18" viewBox="0 0 18 18" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="M4.5 2.5H11l2.500 2.500v10.500h-9z" /><path d="M6.5 8h5M6.5 11h5" /></svg>
            Audit trail
          </a>
          <a routerLink="/admin/system" routerLinkActive="on">
            <svg aria-hidden="true" width="18" height="18" viewBox="0 0 18 18" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="M2 9h3l2-5 3.500 10 2-5H16" /></svg>
            System status
          </a>
        </div>
      }

      <div class="foot">
        <span class="live" [class.on]="live.connected()" [title]="live.connected() ? 'Figures update as they change' : 'No live connection: pages refresh every 15 seconds'" data-testid="live-state">
          <i></i>{{ live.connected() ? 'Live' : 'Refreshing every 15 s' }}
        </span>
        <div class="who" nz-dropdown [nzDropdownMenu]="menu" nzTrigger="click" nzPlacement="topLeft" data-testid="user-menu">
          <span class="badge" aria-hidden="true">{{ initials() }}</span>
          <span>
            <b>{{ session()?.displayName || session()?.email }}</b>
            <small>{{ session()?.roles?.join(', ') }}</small>
          </span>
        </div>
        <nz-dropdown-menu #menu="nzDropdownMenu">
          <ul nz-menu>
            <li nz-menu-item nzDisabled>{{ session()?.email }}</li>
            <li nz-menu-divider></li>
            <li nz-menu-item routerLink="/change-password">Change password</li>
            <li nz-menu-item (click)="signOut()">Sign out</li>
          </ul>
        </nz-dropdown-menu>
      </div>
    </nav>
    <main><router-outlet /></main>
  `,
})
export class Shell {
  private readonly store = inject(Store);
  private readonly router = inject(Router);
  protected readonly live = inject(LiveService);

  protected readonly colors = SLEEVE_COLORS;
  protected readonly session = this.store.selectSignal(SessionState.session);
  protected readonly isAdmin = this.store.selectSignal(SessionState.isAdmin);
  protected readonly fundId = this.store.selectSignal(ViewState.fundId);
  protected readonly funds = this.store.selectSignal(ViewState.funds);
  protected readonly sleeves = this.store.selectSignal(ViewState.sleeves);

  /** What is typed in the fund switcher, and whether its list is open. */
  protected readonly search = signal('');
  protected switching = false;
  /** On a narrow screen the links are folded away until the menu button is pressed. */
  protected readonly menuOpen = signal(false);

  /** Following a link in the opened menu folds it away again. */
  protected closeOnLink(event: Event): void {
    if ((event.target as HTMLElement).closest('a')) {
      this.menuOpen.set(false);
    }
  }

  protected readonly current = computed(() => (this.funds() ?? []).find((fund) => fund.id === this.fundId()) ?? null);
  protected readonly otherCount = computed(() => Math.max(0, (this.funds() ?? []).length - (this.current() ? 1 : 0)));
  protected readonly currentKind = computed(() => {
    const fund = this.current();
    if (!fund) {
      return 'Funds and accounts';
    }

    return fund.isDemo ? 'Demo fund' : fund.kind === 'Sma' ? 'Managed account' : 'Fund';
  });
  protected readonly shownFunds = computed(() => {
    const wanted = this.search().trim().toLowerCase();
    return (this.funds() ?? []).filter((fund) => wanted === '' || fund.name.toLowerCase().includes(wanted));
  });
  protected readonly fundInitials = computed(() => initialsOf(this.current()?.name ?? 'K'));
  protected readonly initials = computed(() => initialsOf(this.session()?.displayName || this.session()?.email || ''));

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

/** "Liquid Master Fund" → "LM"; an email gives the letters before and after the dot or the at sign. */
function initialsOf(name: string): string {
  return name
    .split(/[\s@.]+/)
    .filter((part) => part.length > 0)
    .slice(0, 2)
    .map((part) => part[0].toUpperCase())
    .join('');
}
