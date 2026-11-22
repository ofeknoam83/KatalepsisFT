import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { AssetPerformance, Performance, PerformancePeriod, PerformanceScope } from './models';

@Injectable({ providedIn: 'root' })
export class PerformanceApi {
  private readonly http = inject(HttpClient);

  get(scope: PerformanceScope, id: number, benchmarkId: number | null, period: PerformancePeriod): Observable<Performance> {
    return this.http.get<Performance>(`/api/performance/${scope}/${id}`, { params: this.params(benchmarkId, period) });
  }

  asset(symbol: string, benchmarkId: number | null, period: PerformancePeriod): Observable<AssetPerformance> {
    return this.http.get<AssetPerformance>(`/api/performance/asset/${encodeURIComponent(symbol)}`, { params: this.params(benchmarkId, period) });
  }

  private params(benchmarkId: number | null, period: PerformancePeriod): HttpParams {
    const params = new HttpParams().set('period', period);
    return benchmarkId === null ? params : params.set('benchmarkId', benchmarkId);
  }
}
