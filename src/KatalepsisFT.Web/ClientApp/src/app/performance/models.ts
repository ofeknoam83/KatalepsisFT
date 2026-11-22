export type PerformanceScope = 'Fund' | 'Sleeve' | 'Manager';
export type PerformancePeriod = 'Ytd' | 'Days30' | 'Days90' | 'SinceInception';

export interface Benchmark {
  id: number;
  name: string;
  kind: string;
  isApproximation: boolean;
}

export interface Stats {
  totalReturn: number;
  volatility: number | null;
  sharpe: number | null;
  sortino: number | null;
  maxDrawdown: number | null;
  calmar: number | null;
}

export interface Comparison {
  excessReturn: number;
  beta: number | null;
  correlation: number | null;
  trackingError: number | null;
  informationRatio: number | null;
}

export interface PeriodReturn {
  label: string;
  portfolio: number | null;
  benchmark: number | null;
}

export interface MonthlyReturn {
  month: string;
  portfolio: number;
  benchmark: number;
}

export interface ChildPerformance {
  kind: PerformanceScope;
  id: number;
  name: string;
  navUsd: number;
  stats: Stats;
  comparison: Comparison;
}

export interface PnlLine {
  key: string;
  pnlUsd: number;
}

export interface Attribution {
  startNavUsd: number;
  navChangeUsd: number;
  explainedUsd: number;
  byAsset: PnlLine[];
  byType: PnlLine[];
  byChild: PnlLine[];
}

export interface Performance {
  scope: { kind: PerformanceScope; id: number; name: string; fundId: number; fundName: string; isDemo: boolean; sleeveId: number | null; sleeveName: string | null };
  benchmark: Benchmark;
  benchmarks: Benchmark[];
  period: PerformancePeriod;
  from: string;
  asOf: string;
  dates: string[];
  portfolio: number[];
  benchmarkSeries: number[];
  drawdown: number[];
  stats: Stats;
  benchmarkStats: Stats;
  comparison: Comparison;
  periods: PeriodReturn[];
  monthly: MonthlyReturn[];
  children: ChildPerformance[];
  attribution: Attribution;
}

export interface AssetPerformance {
  symbol: string;
  benchmark: Benchmark;
  benchmarks: Benchmark[];
  period: PerformancePeriod;
  from: string;
  asOf: string;
  dates: string[];
  asset: number[];
  benchmarkSeries: number[];
  stats: Stats;
  benchmarkStats: Stats;
  comparison: Comparison;
}

export const PERIODS: { value: PerformancePeriod; label: string }[] = [
  { value: 'Days30', label: '30D' },
  { value: 'Days90', label: '90D' },
  { value: 'Ytd', label: 'YTD' },
  { value: 'SinceInception', label: 'All' },
];
