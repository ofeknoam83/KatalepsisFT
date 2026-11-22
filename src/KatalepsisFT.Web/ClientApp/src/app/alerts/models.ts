export type AlertStatus = 'Open' | 'Acknowledged' | 'Resolved';
export type AlertSeverity = 'Info' | 'Warning' | 'Critical';
export type AlertScopeKind = 'Fund' | 'Sleeve' | 'Manager';
export type AlertOperator = 'Above' | 'Below';
export type AlertListState = 'Active' | 'Resolved' | 'All';
export type AlertMetric =
  | 'GrossExposureOverNav'
  | 'NetExposureOverNav'
  | 'LargestAssetOverNav'
  | 'DeepestManagerDrawdown'
  | 'SlowestDaysToLiquidate'
  | 'ValueAtRiskOverNav'
  | 'DayReturn';

export const METRICS: { value: AlertMetric; label: string }[] = [
  { value: 'GrossExposureOverNav', label: 'Gross exposure ÷ NAV' },
  { value: 'NetExposureOverNav', label: 'Net exposure ÷ NAV' },
  { value: 'LargestAssetOverNav', label: 'Largest single asset ÷ NAV' },
  { value: 'DeepestManagerDrawdown', label: 'Deepest manager drawdown' },
  { value: 'SlowestDaysToLiquidate', label: 'Slowest asset, days to liquidate' },
  { value: 'ValueAtRiskOverNav', label: '1-day value at risk ÷ NAV' },
  { value: 'DayReturn', label: 'Day return' },
];

export interface AlertAction {
  kind: 'Raised' | 'Acknowledged' | 'Assigned' | 'Commented' | 'Snoozed' | 'Resolved' | 'Recovered';
  at: string;
  userName: string | null;
  note: string | null;
}

export interface Alert {
  id: number;
  ruleId: number;
  status: AlertStatus;
  severity: AlertSeverity;
  scopeKind: AlertScopeKind;
  scopeId: number;
  scopeName: string;
  sleeveId: number | null;
  metric: AlertMetric;
  description: string;
  subject: string | null;
  value: number;
  threshold: number;
  fromMandate: boolean;
  dataDate: string;
  raisedAt: string;
  lastSeenAt: string;
  resolvedAt: string | null;
  assigneeUserId: number | null;
  assigneeName: string | null;
  snoozedUntil: string | null;
  isSnoozed: boolean;
  actions: AlertAction[];
}

export interface AlertCounts {
  active: number;
  critical: number;
  needingAttention: number;
  unassigned: number;
  snoozed: number;
}

export interface AlertList {
  counts: AlertCounts;
  alerts: Alert[];
}

export interface AlertRule {
  id: number;
  scopeKind: AlertScopeKind;
  scopeId: number;
  scopeName: string;
  metric: AlertMetric;
  operator: AlertOperator;
  threshold: number;
  severity: AlertSeverity;
  isEnabled: boolean;
  fromMandate: boolean;
  description: string;
  hasActiveAlert: boolean;
}

export interface AlertScopeOption {
  kind: AlertScopeKind;
  id: number;
  name: string;
}

export interface AlertRules {
  rules: AlertRule[];
  scopes: AlertScopeOption[];
}

export type SaveAlertRule = Pick<AlertRule, 'scopeKind' | 'scopeId' | 'metric' | 'operator' | 'threshold' | 'severity' | 'isEnabled'>;

export interface Assignee {
  id: number;
  name: string;
}

export const SEVERITY_COLOR: Record<AlertSeverity, string> = { Critical: 'red', Warning: 'orange', Info: 'blue' };
