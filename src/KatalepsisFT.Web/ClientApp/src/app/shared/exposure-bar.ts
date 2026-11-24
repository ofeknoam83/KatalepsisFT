import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

/** Long to the right of the centre line, short to the left, on a scale shared by the rows of one table. */
@Component({
  selector: 'app-exposure-bar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  styles: `
    :host {
      display: block;
      min-width: 140px;
    }
    .track {
      position: relative;
      height: 14px;
      background: linear-gradient(to right, transparent calc(50% - 0.5px), #98a2b3 calc(50% - 0.5px), #98a2b3 calc(50% + 0.5px), transparent calc(50% + 0.5px));
    }
    .long,
    .short {
      position: absolute;
      top: 3px;
      height: 8px;
    }
    .long {
      left: calc(50% + 1px);
      background: var(--kft-brand);
      border-radius: 0 4px 4px 0;
    }
    .short {
      right: calc(50% + 1px);
      background: #f0a070;
      border-radius: 4px 0 0 4px;
    }
  `,
  template: `
    <div class="track" [attr.aria-label]="'long ' + long() + ', short ' + short()">
      <div class="short" [style.width.%]="shortWidth()"></div>
      <div class="long" [style.width.%]="longWidth()"></div>
    </div>
  `,
})
export class ExposureBar {
  readonly long = input.required<number>();
  readonly short = input.required<number>();
  /** The largest long or short among the rows, so bars are comparable. */
  readonly scale = input.required<number>();

  protected readonly longWidth = computed(() => (this.scale() > 0 ? Math.min(49, (this.long() / this.scale()) * 49) : 0));
  protected readonly shortWidth = computed(() => (this.scale() > 0 ? Math.min(49, (-this.short() / this.scale()) * 49) : 0));
}
