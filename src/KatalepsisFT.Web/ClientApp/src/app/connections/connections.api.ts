import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

export type Venue = 'Binance' | 'Bybit' | 'Okx' | 'Deribit' | 'CoinbaseExchange' | 'CoinbasePrime' | 'Kucoin' | 'Mexc' | 'Hyperliquid' | 'EvmWallet' | 'BitcoinWallet' | 'SolanaWallet' | 'TronWallet' | 'Bitget' | 'Gate' | 'Kraken';
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
  transfersError: string | null;
  notes: string[];
}

export interface Connections {
  canAddKeys: boolean;
  venues: { venue: Venue; name: string; needsPassphrase: boolean; usesAddress: boolean }[];
  managers: { id: number; name: string; sleeve: string }[];
  connections: Connection[];
}

export interface AddConnection {
  managerId: number;
  venue: Venue;
  label: string;
  apiKey: string;
  apiSecret: string | null;
  passphrase: string | null;
}

export interface NewFund {
  name: string;
  kind: 'Fund' | 'Sma';
  ownerName: string | null;
  benchmarkId: number | null;
  sleeves: { name: string; mandateType: MandateType; managers: { name: string; allocatedCapitalUsd: number }[] }[];
}

export type LimitKind = 'GrossExposureMax' | 'NetExposureMin' | 'NetExposureMax' | 'SingleAssetMax' | 'ManagerDrawdownMin' | 'DaysToLiquidateMax';

export interface StructureManager {
  id: number;
  name: string;
  allocatedCapitalUsd: number;
  dataSource: string;
  hasHistory: boolean;
  connections: number;
  /** The day the manager was retired; null while it is active. */
  retiredOn: string | null;
}

export interface StructureSleeve {
  id: number;
  name: string;
  mandateType: MandateType;
  limits: { kind: LimitKind; value: number }[];
  managers: StructureManager[];
}

export interface Structure {
  id: number;
  name: string;
  benchmarkId: number | null;
  canEdit: boolean;
  benchmarks: { id: number; name: string }[];
  sleeves: StructureSleeve[];
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

  structure(fundId: number): Observable<Structure> {
    return this.http.get<Structure>(`/api/funds/${fundId}/structure`);
  }

  saveFund(fundId: number, name: string, benchmarkId: number | null): Observable<void> {
    return this.http.put<void>(`/api/funds/${fundId}`, { name, benchmarkId });
  }

  addSleeve(fundId: number, name: string, mandateType: MandateType): Observable<void> {
    return this.http.post<void>(`/api/funds/${fundId}/sleeves`, { name, mandateType, limits: null });
  }

  saveSleeve(sleeveId: number, name: string, mandateType: MandateType, limits: { kind: LimitKind; value: number }[]): Observable<void> {
    return this.http.put<void>(`/api/sleeves/${sleeveId}`, { name, mandateType, limits });
  }

  removeSleeve(sleeveId: number): Observable<void> {
    return this.http.delete<void>(`/api/sleeves/${sleeveId}`);
  }

  addManager(sleeveId: number, name: string, allocatedCapitalUsd: number): Observable<{ id: number }> {
    return this.http.post<{ id: number }>(`/api/sleeves/${sleeveId}/managers`, { name, allocatedCapitalUsd });
  }

  saveManager(managerId: number, name: string, allocatedCapitalUsd: number): Observable<void> {
    return this.http.put<void>(`/api/managers/${managerId}`, { name, allocatedCapitalUsd });
  }

  moveManager(managerId: number, sleeveId: number): Observable<void> {
    return this.http.post<void>(`/api/managers/${managerId}/move`, { sleeveId });
  }

  retireManager(managerId: number): Observable<void> {
    return this.http.post<void>(`/api/managers/${managerId}/retire`, {});
  }

  reinstateManager(managerId: number): Observable<void> {
    return this.http.post<void>(`/api/managers/${managerId}/reinstate`, {});
  }

  removeManager(managerId: number): Observable<void> {
    return this.http.delete<void>(`/api/managers/${managerId}`);
  }

  createFund(request: NewFund): Observable<{ id: number; name: string }> {
    return this.http.post<{ id: number; name: string }>('/api/funds', request);
  }
}
