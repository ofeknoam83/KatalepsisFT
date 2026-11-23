import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, inject, input } from '@angular/core';
import { toObservable } from '@angular/core/rxjs-interop';
import { NzTableModule } from 'ng-zorro-antd/table';
import { LiveService } from '../core/live.service';
import { FORMAT_PIPES } from '../shared/format';
import { keptData } from '../shared/page-data';

interface VenueExposure {
  navUsd: number;
  venues: { name: string; isVenue: boolean; navUsd: number; share: number; accounts: number; managers: string[] }[];
}

/**
 * Where the fund's money sits: its NAV split by the exchange or wallet type that holds it. One venue failing touches
 * every manager that keeps money there, whatever sleeve they are in, so this is read across the whole fund.
 */
@Component({
  selector: 'kft-venue-exposure',
  imports: [NzTableModule, ...FORMAT_PIPES],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (loaded.data(); as v) {
      @if (v.venues.length > 0) {
        <div class="panel" data-testid="venue-exposure">
          <h3>Where the money sits <span class="hint">NAV by the exchange or wallet type holding it · an alert rule can limit the largest share</span></h3>
          <nz-table [nzData]="v.venues" nzTemplateMode [nzShowPagination]="false" nzSize="small">
            <thead>
              <tr><th>Held on</th><th class="num">Value</th><th class="num">Share of fund</th><th style="width:26%"></th><th class="num">Accounts</th><th>Managers</th></tr>
            </thead>
            <tbody>
              @for (row of v.venues; track row.name) {
                <tr>
                  <td>
                    <b>{{ row.name }}</b>
                    @if (!row.isVenue) {
                      <span style="color:#8c8c8c;font-size:12px"> · not on a connected venue</span>
                    }
                  </td>
                  <td class="num">{{ row.navUsd | usd }}</td>
                  <td class="num">{{ row.share | pct: 1 : false }}</td>
                  <td><div class="bar"><i [style.width.%]="width(row.share)" [class.plain]="!row.isVenue"></i></div></td>
                  <td class="num">{{ row.accounts || '–' }}</td>
                  <td style="color:#595959">{{ row.managers.join(', ') }}</td>
                </tr>
              }
            </tbody>
          </nz-table>
        </div>
      }
    }
  `,
  styles: `
    .bar {
      height: 8px;
      background: #f0f0f0;
      border-radius: 2px;

      i {
        display: block;
        height: 8px;
        border-radius: 2px;
        background: #2a78d6;

        &.plain {
          background: #bfbfbf;
        }
      }
    }
  `,
})
export class VenueExposurePanel {
  readonly fundId = input.required<number>();

  private readonly http = inject(HttpClient);
  protected readonly loaded = keptData(
    toObservable(this.fundId),
    toObservable(inject(LiveService).slowTick),
    (id) => this.http.get<VenueExposure>(`/api/funds/${id}/venues`),
    (id) => id,
  );

  protected width(share: number): number {
    return Math.max(0, Math.min(100, share * 100));
  }
}
