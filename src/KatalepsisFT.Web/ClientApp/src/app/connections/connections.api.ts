import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

export type Venue = 'Binance' | 'Bybit' | 'Okx' | 'Deribit';
export type ConnectionState = 'Pending' | 'Live' | 'Failed' | 'Disabled';
export type MandateType = 'Beta' | 'MarketNeutral' | 'Directional' | 'Other';

export interface Connection {
  id: number;
  managerId: number;
  manager: string;
  sleeve: string;
  venue: Venue;
  venueName: string;
  label: string;
  keyHint: string;
  permissionsVerified: boolean;
  state: ConnectionState;
  lastAttemptAt: string | null;
  lastSuccessAt: string | null;
  lastError: string | null;
  positions: number;
  computedNavUsd: number | null;
  reportedEquityUsd: number | null;
}

export interface Connections {
  canAddKeys: boolean;
  venues: { venue: Venue; name: string; needsPassphrase: boolean }[];
  managers: { id: number; name: string; sleeve: string }[];
  connections: Connection[];
}

export interface AddConnection {
  managerId: number;
  venue: Venue;
  label: string;
  apiKey: string;
  apiSecret: string;
  passphrase: string | null;
}

export interface NewFund {
  name: string;
  benchmarkId: number | null;
  sleeves: { name: string; mandateType: MandateType; managers: { name: string; allocatedCapitalUsd: number }[] }[];
}

@Injectable({ providedIn: 'root' })
export class ConnectionsApi {
  private readonly http = inject(HttpClient);

  list(fundId: number): Observable<Connections> {
    return this.http.get<Connections>(`/api/funds/${fundId}/connections`);
  }

  add(request: AddConnection): Observable<Connection> {
    return this.http.post<Connection>('/api/connections', request);
  }

  sync(id: number): Observable<Connection> {
    return this.http.post<Connection>(`/api/connections/${id}/sync`, {});
  }

  setEnabled(id: number, enabled: boolean): Observable<Connection> {
    return this.http.post<Connection>(`/api/connections/${id}/enabled`, { enabled });
  }

  remove(id: number): Observable<void> {
    return this.http.delete<void>(`/api/connections/${id}`);
  }

  createFund(request: NewFund): Observable<{ id: number; name: string }> {
    return this.http.post<{ id: number; name: string }>('/api/funds', request);
  }
}
