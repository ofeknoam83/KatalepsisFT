export type InstrumentFilter = 'All' | 'Spot' | 'Perp' | 'Option' | 'TradFi';
export type InstrumentType = 'Spot' | 'Perp' | 'Future' | 'Option' | 'Equity' | 'Cash';

export interface Totals {
  navUsd: number;
  longUsd: number;
  shortUsd: number;
  grossUsd: number;
  netUsd: number;
  cashUsd: number;
}

export interface FundRef {
  id: number;
  name: string;
  isDemo: boolean;
  benchmark: string | null;
}

export interface SleeveRef {
  id: number;
  name: string;
  mandateType: string;
}

export interface ManagerRef {
  id: number;
  name: string;
  sleeveId: number;
}

export interface FundListItem {
  id: number;
  name: string;
  isDemo: boolean;
  navUsd: number;
  asOf: string | null;
  sleeves: number;
  managers: number;
}

export interface TypeExposure {
  type: InstrumentType;
  longUsd: number;
  shortUsd: number;
  grossUsd: number;
  netUsd: number;
}

export interface AssetPrice {
  symbol: string;
  name: string;
  price: number | null;
  change1D: number | null;
  change30D: number | null;
  changeYtd: number | null;
  averageDailyVolumeUsd: number | null;
}

export interface Holder {
  managerId: number;
  manager: string;
  netUsd: number;
}

export interface AssetRow {
  asset: AssetPrice;
  longUsd: number;
  shortUsd: number;
  grossUsd: number;
  netUsd: number;
  netByType: Partial<Record<InstrumentType, number>>;
  holders: Holder[];
  sleeveIds: number[];
  hasOffsettingManagers: boolean;
}

export interface SleeveRow {
  sleeve: SleeveRef;
  managers: number;
  totals: Totals;
}

export interface FundOverview {
  fund: FundRef;
  asOf: string;
  totals: Totals;
  sleeves: SleeveRow[];
  topAssets: AssetRow[];
  byType: TypeExposure[];
}

export interface FundHoldings {
  fund: FundRef;
  asOf: string;
  totals: Totals;
  sleeves: SleeveRef[];
  assets: AssetRow[];
}

export interface MandateLimit {
  kind: string;
  value: number;
}

export interface ManagerRow {
  manager: ManagerRef;
  dataSource: string;
  totals: Totals;
}

export interface OverlapPair {
  managerA: number;
  managerB: number;
  share: number;
  oppositeSides: number;
}

export interface SleeveDetail {
  fund: FundRef;
  sleeve: SleeveRef;
  asOf: string;
  fundNavUsd: number;
  totals: Totals;
  limits: MandateLimit[];
  managers: ManagerRow[];
  assets: AssetRow[];
  byType: TypeExposure[];
  overlaps: OverlapPair[];
}

export interface Position {
  asset: string;
  instrument: string;
  type: InstrumentType;
  quantity: number;
  entryPrice: number | null;
  markPrice: number;
  marketValueUsd: number;
  exposureUsd: number;
  unrealizedPnlUsd: number;
}

export interface ManagerDetail {
  fund: FundRef;
  sleeve: SleeveRef;
  manager: ManagerRef;
  dataSource: string;
  inceptionDate: string;
  asOf: string;
  sleeveNavUsd: number;
  fundNavUsd: number;
  totals: Totals;
  positions: Position[];
  assets: AssetRow[];
}

export interface AssetPosition {
  manager: ManagerRef;
  managerNavUsd: number;
  instrument: string;
  type: InstrumentType;
  quantity: number;
  exposureUsd: number;
}

export interface AssetSleeve {
  sleeve: SleeveRef;
  sleeveNavUsd: number;
  longUsd: number;
  shortUsd: number;
  positions: AssetPosition[];
}

export interface AssetDetail {
  fund: FundRef;
  asset: AssetPrice;
  asOf: string;
  fundNavUsd: number;
  longUsd: number;
  shortUsd: number;
  grossUsd: number;
  netUsd: number;
  managers: number;
  sleeves: AssetSleeve[];
  byType: TypeExposure[];
}

/** Sleeves are told apart by a fixed colour per position in the fund, never by rank. */
export const SLEEVE_COLORS = ['#2a78d6', '#eb6834', '#1baf7a', '#eda100', '#e87ba4', '#008300', '#6250d6', '#e34948'];
