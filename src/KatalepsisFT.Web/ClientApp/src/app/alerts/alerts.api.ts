import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { Alert, AlertList, AlertListState, AlertRule, AlertRules, Assignee, SaveAlertRule } from './models';

@Injectable({ providedIn: 'root' })
export class AlertsApi {
  private readonly http = inject(HttpClient);

  list(fundId: number, state: AlertListState, sleeveId: number | null): Observable<AlertList> {
    const params: Record<string, string | number> = sleeveId === null ? { state } : { state, sleeveId };
    return this.http.get<AlertList>(`/api/funds/${fundId}/alerts`, { params });
  }

  evaluate(fundId: number): Observable<{ raised: number; recovered: number; active: number }> {
    return this.http.post<{ raised: number; recovered: number; active: number }>(`/api/funds/${fundId}/alerts/evaluate`, {});
  }

  assignees(): Observable<Assignee[]> {
    return this.http.get<Assignee[]>('/api/alerts/assignees');
  }

  acknowledge(id: number): Observable<Alert> {
    return this.http.post<Alert>(`/api/alerts/${id}/acknowledge`, {});
  }

  assign(id: number, userId: number | null): Observable<Alert> {
    return this.http.post<Alert>(`/api/alerts/${id}/assign`, { userId });
  }

  comment(id: number, note: string): Observable<Alert> {
    return this.http.post<Alert>(`/api/alerts/${id}/comment`, { note });
  }

  snooze(id: number, days: number): Observable<Alert> {
    return this.http.post<Alert>(`/api/alerts/${id}/snooze`, { days, note: null });
  }

  resolve(id: number, note: string | null): Observable<Alert> {
    return this.http.post<Alert>(`/api/alerts/${id}/resolve`, { note });
  }

  rules(fundId: number): Observable<AlertRules> {
    return this.http.get<AlertRules>(`/api/funds/${fundId}/alert-rules`);
  }

  createRule(fundId: number, rule: SaveAlertRule): Observable<AlertRule> {
    return this.http.post<AlertRule>(`/api/funds/${fundId}/alert-rules`, rule);
  }

  updateRule(id: number, rule: SaveAlertRule): Observable<AlertRule> {
    return this.http.put<AlertRule>(`/api/alert-rules/${id}`, rule);
  }

  deleteRule(id: number): Observable<void> {
    return this.http.delete<void>(`/api/alert-rules/${id}`);
  }
}
