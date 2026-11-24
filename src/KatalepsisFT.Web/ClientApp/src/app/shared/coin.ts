import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { COIN_ICONS } from './coin-list';

/**
 * A token's own logo, from the icon files served under /coins (the web3icons set, MIT licence). A symbol the set
 * does not have, such as a share, gets a round badge with its letters instead.
 */
@Component({
  selector: 'app-coin',
  changeDetection: ChangeDetectionStrategy.OnPush,
  styles: `
    :host {
      display: inline-flex;
      vertical-align: middle;
      margin-right: 8px;
    }
    img,
    span {
      width: 22px;
      height: 22px;
      border-radius: 50%;
    }
    span {
      display: grid;
      place-items: center;
      background: var(--kft-fill);
      color: var(--kft-text-2);
      font-size: 8px;
      font-weight: 700;
      letter-spacing: -0.02em;
    }
  `,
  template: `
    @if (file(); as name) {
      <img [src]="'/coins/' + name + '.svg'" alt="" loading="lazy" />
    } @else {
      <span aria-hidden="true">{{ letters() }}</span>
    }
  `,
})
export class Coin {
  readonly symbol = input.required<string>();

  /**
   * The icon file for this symbol, or null when there is none. Hyperliquid quotes some coins per thousand and puts
   * a "k" in front (kSHIB, kBONK); those take the coin's own icon.
   */
  protected readonly file = computed(() => {
    const symbol = this.symbol().toUpperCase();
    if (COIN_ICONS.has(symbol)) {
      return symbol;
    }

    // Only a small k followed by capitals is read this way, so KAVA or KAITO are never mistaken for it.
    return /^k[A-Z0-9]{2,}$/.test(this.symbol()) && COIN_ICONS.has(symbol.slice(1)) ? symbol.slice(1) : null;
  });
  protected readonly letters = computed(() => this.symbol().slice(0, 4).toUpperCase());
}
