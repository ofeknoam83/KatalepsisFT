import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';

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
    @if (missing() === symbol()) {
      <span aria-hidden="true">{{ letters() }}</span>
    } @else {
      <img [src]="'/coins/' + file() + '.svg'" alt="" loading="lazy" (error)="missing.set(symbol())" />
    }
  `,
})
export class Coin {
  readonly symbol = input.required<string>();

  /** The symbol whose icon failed to load; kept by name so a row reused for another asset tries again. */
  protected readonly missing = signal<string | null>(null);
  protected readonly file = computed(() => encodeURIComponent(this.symbol().toUpperCase()));
  protected readonly letters = computed(() => this.symbol().slice(0, 4).toUpperCase());
}
