import { Totals } from '../funds/models';
import { Performance, PerformanceScope } from '../performance/models';

export type Scope = Performance['scope'];

export interface AssetBeta {
  asset: string;
  netUsd: number;
  grossUsd: number;
  beta: number;
  betaIsAssumed: boolean;
}

export interface ScenarioGrid {
  referenceAsset: string;
  shocks: number[];
  betaMultipliers: number[];
  cells: number[][];
}

export interface ScenarioRow {
  kind: PerformanceScope;
  id: number;
  name: string;
  navUsd: number;
  pnl: number[];
}

export interface MandateCheck {
  sleeveId: number;
  sleeve: string;
  kind: string;
  rule: string;
  current: number | null;
  limit: number;
  withinLimit: boolean | null;
}

export interface VolatilityRow {
  kind: PerformanceScope;
  id: number;
  name: string;
  days30: number | null;
  days90: number | null;
  shareOfVariance: number | null;
}

export interface Risk {
  scope: Scope;
  asOf: string;
  totals: Totals;
  var: { valueAtRiskUsd: number; expectedShortfallUsd: number; days: number; confidence: number };
  topFiveShareOfGross: number;
  scenario: ScenarioGrid;
  scenarioByChild: ScenarioRow[];
  assets: AssetBeta[];
  mandate: MandateCheck[];
  volatility: VolatilityRow[];
  rollingDates: string[];
  rollingVolatility: { name: string; values: (number | null)[] }[];
  correlation: { names: string[]; matrix: (number | null)[][] };
}

export interface Ladder {
  withinOneDayUsd: number;
  oneToFiveDaysUsd: number;
  fiveToTwentyDaysUsd: number;
  beyondTwentyDaysUsd: number;
  unknownUsd: number;
  totalUsd: number;
}

export interface LiquidityLine {
  asset: string;
  grossUsd: number;
  averageDailyVolumeUsd: number | null;
  dailyCapacityUsd: number | null;
  days: number | null;
  managers: number;
}

export interface Liquidity {
  scope: Scope;
  asOf: string;
  participation: number;
  navUsd: number;
  ladder: Ladder;
  children: { kind: PerformanceScope; id: number; name: string; ladder: Ladder }[];
  lines: LiquidityLine[];
  slowestQueue: { manager: string; instrument: string; exposureUsd: number; daysAlone: number | null }[];
}
