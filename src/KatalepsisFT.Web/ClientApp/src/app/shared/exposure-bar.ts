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
      height: 10px;
      background: linear-gradient(to right, transparent calc(50% - 0.5px), #c3c2b7 calc(50% - 0.5px), #c3c2b7 calc(50% + 0.5px), transparent calc(50% + 0.5px));
    }
    .long,
    .short {
      position: absolute;
      top: 0;
      height: 10px;
    }
    .long {
      left: calc(50% + 1px);
      background: #2a78d6;
      border-radius: 0 3px 3px 0;
    }
    .short {
      right: calc(50% + 1px);
      background: #e34948;
      border-radius: 3px 0 0 3px;
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
