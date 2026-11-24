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
      flex-wrap: wrap;
      gap: 10px 14px;
      align-items: center;
      padding: 12px 16px;
      margin-bottom: 18px;
      border-radius: var(--kft-radius);
      border: 1px solid #f3d3cf;
      background: var(--kft-card);
      box-shadow: var(--kft-shadow);
      color: var(--kft-text);
    }
    a.calm {
      border-color: #f3e2b3;
    }
    .icon {
      flex: 0 0 34px;
      height: 34px;
      border-radius: 10px;
      background: var(--kft-down-soft);
      color: var(--kft-down);
      display: grid;
      place-items: center;
    }
    a.calm .icon {
      background: var(--kft-warn-soft);
      color: var(--kft-warn);
    }
    .text {
      flex: 1 1 380px;
      min-width: 0;
    }
    b {
      font-weight: 600;
    }
    .critical {
      font-size: 12px;
      font-weight: 600;
      color: var(--kft-down);
      background: var(--kft-down-soft);
      padding: 2px 8px;
      border-radius: 999px;
      margin-left: 6px;
    }
    .summary {
      display: block;
      font-size: 13px;
      color: var(--kft-text-2);
      margin-top: 2px;
    }
    .more {
      display: inline-flex;
      align-items: center;
      min-height: 36px;
      padding: 0 14px;
      border: 1px solid #d5d9e0;
      border-radius: 9px;
      font-weight: 600;
      font-size: 13px;
    }
  `,
  template: `
    @if (list(); as l) {
      @if (l.counts.active > 0) {
        <a [routerLink]="['/funds', fundId(), 'alerts']" [queryParams]="sleeveId() === null ? {} : { sleeve: sleeveId() }" [class.calm]="l.counts.critical === 0" data-testid="alert-strip">
          <span class="icon" aria-hidden="true">
            <svg width="18" height="18" viewBox="0 0 18 18" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M9 2.5l7 12.5H2z" /><path d="M9 7.5V11M9 13.2v.1" /></svg>
          </span>
          <span class="text">
            <b>{{ l.counts.active }} active {{ l.counts.active === 1 ? 'alert' : 'alerts' }}</b>
            @if (l.counts.critical > 0) {
              <span class="critical">{{ l.counts.critical }} critical</span>
            }
            <span class="summary">{{ summary() }}</span>
          </span>
          <span class="more">Open the inbox</span>
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
    toObservable(inject(LiveService).slowTick),
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
    return `${first.scopeName}: ${first.description}${rest > 0 ? ` · and ${rest} more` : ''}`;
  });
}
