import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Store } from '@ngxs/store';
import { Observable, tap } from 'rxjs';
import { SetFundSleeves } from './view.state';
import { AssetDetail, Intraday, FundHoldings, FundListItem, FundOverview, InstrumentFilter, ManagerDetail, SleeveDetail } from './models';

@Injectable({ providedIn: 'root' })
export class FundsApi {
  private readonly http = inject(HttpClient);
  private readonly store = inject(Store);

  funds(): Observable<FundListItem[]> {
    return this.http.get<FundListItem[]>('/api/funds');
  }

  /** Every fund page loads this, so it is also where the navigation learns which fund is open and what its sleeves are. */
  overview(fundId: number, filter: InstrumentFilter): Observable<FundOverview> {
    return this.http
      .get<FundOverview>(`/api/funds/${fundId}/overview`, { params: { filter } })
      .pipe(tap((d) => this.store.dispatch(new SetFundSleeves(d.fund.id, d.sleeves.map((s) => s.sleeve)))));
  }

  /** NAV and exposure on a five-minute grid; empty for funds that are not read from venues. */
  intraday(fundId: number, hours: number): Observable<Intraday> {
    return this.http.get<Intraday>(`/api/funds/${fundId}/intraday`, { params: { hours } });
  }

  holdings(fundId: number, filter: InstrumentFilter): Observable<FundHoldings> {
    return this.http.get<FundHoldings>(`/api/funds/${fundId}/holdings`, { params: { filter } });
  }

  sleeve(sleeveId: number, filter: InstrumentFilter): Observable<SleeveDetail> {
    return this.http.get<SleeveDetail>(`/api/sleeves/${sleeveId}`, { params: { filter } });
  }

  manager(managerId: number, filter: InstrumentFilter): Observable<ManagerDetail> {
    return this.http.get<ManagerDetail>(`/api/managers/${managerId}`, { params: { filter } });
  }

  asset(fundId: number, symbol: string): Observable<AssetDetail> {
    return this.http.get<AssetDetail>(`/api/funds/${fundId}/assets/${encodeURIComponent(symbol)}`);
  }
}
