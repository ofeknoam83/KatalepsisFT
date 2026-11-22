import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { PerformanceScope } from '../performance/models';
import { Liquidity, Risk } from './models';

@Injectable({ providedIn: 'root' })
export class RiskApi {
  private readonly http = inject(HttpClient);

  risk(scope: PerformanceScope, id: number): Observable<Risk> {
    return this.http.get<Risk>(`/api/risk/${scope}/${id}`);
  }

  liquidity(scope: PerformanceScope, id: number, participation: number): Observable<Liquidity> {
    return this.http.get<Liquidity>(`/api/liquidity/${scope}/${id}`, { params: { participation } });
  }
}
