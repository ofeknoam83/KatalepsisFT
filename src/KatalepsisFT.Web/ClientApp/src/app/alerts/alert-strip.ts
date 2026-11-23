import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { toObservable } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { LiveService } from '../core/live.service';
import { combineLatest } from 'rxjs';
import { keptData } from '../shared/page-data';
import { AlertsApi } from './alerts.api';

/** One line saying how many alerts are active for a fund or one of its sleeves, linking to the inbox. Nothing when there are none. */
@Component({
  selector: 'app-alert-strip',
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styles: `
    a {
      display: flex;
      gap: 10px;
      align-items: baseline;
      padding: 9px 14px;
      margin-bottom: 14px;
      border-radius: 8px;
      border: 1px solid #ffccc7;
      background: #fff2f0;
      color: #1f1f1f;
      font-size: 13px;
    }
    a.calm {
      border-color: #ffe58f;
      background: #fffbe6;
    }
    b {
      color: #cf1322;
    }
    a.calm b {
      color: #ad6800;
    }
    span.more {
      margin-left: auto;
      color: #1677ff;
    }
  `,
  template: `
    @if (list(); as l) {
      @if (l.counts.active > 0) {
        <a [routerLink]="['/funds', fundId(), 'alerts']" [queryParams]="sleeveId() === null ? {} : { sleeve: sleeveId() }" [class.calm]="l.counts.critical === 0" data-testid="alert-strip">
          <b>{{ l.counts.active }} active {{ l.counts.active === 1 ? 'alert' : 'alerts' }}</b>
          <span>{{ summary() }}</span>
          <span class="more">Open the inbox →</span>
        </a>
      }
    }
  `,
})
export class AlertStrip {
  private readonly api = inject(AlertsApi);

  readonly fundId = input.required<number>();
  readonly sleeveId = input<number | null>(null);

  protected readonly list = keptData(
    combineLatest([toObservable(this.fundId), toObservable(this.sleeveId)]),
    toObservable(inject(LiveService).tick),
    ([fundId, sleeveId]) => this.api.list(fundId, 'Active', sleeveId),
    ([fundId, sleeveId]) => `${fundId}:${sleeveId}`,
  ).data;
  protected readonly summary = computed(() => {
    const l = this.list();
    if (!l) {
      return '';
    }

    const first = l.alerts[0];
    const rest = l.counts.active - 1;
    return `${l.counts.critical} critical · ${first.scopeName}: ${first.description}${rest > 0 ? ` · and ${rest} more` : ''}`;
  });
}
