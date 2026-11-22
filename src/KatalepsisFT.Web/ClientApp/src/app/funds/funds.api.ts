import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { AssetDetail, FundHoldings, FundListItem, FundOverview, InstrumentFilter, ManagerDetail, SleeveDetail } from './models';

@Injectable({ providedIn: 'root' })
export class FundsApi {
  private readonly http = inject(HttpClient);

  funds(): Observable<FundListItem[]> {
    return this.http.get<FundListItem[]>('/api/funds');
  }

  overview(fundId: number, filter: InstrumentFilter): Observable<FundOverview> {
    return this.http.get<FundOverview>(`/api/funds/${fundId}/overview`, { params: { filter } });
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
