export type TradingVenue = 'Binance' | 'Bybit' | 'Okx' | 'Hyperliquid' | 'Kucoin' | 'Bitget' | 'Gate' | 'Mexc' | 'CoinbaseExchange' | 'Kraken' | 'Deribit';
export type TradedType = 'Spot' | 'Perp';

export interface LiveInstrument {
  asset: string;
  type: TradedType;
  venues: TradingVenue[];
}

/** "BTC-PERP" for a perpetual, "BTC spot" for spot. */
export function instrumentName(asset: string, type: string): string {
  return type === 'Spot' ? `${asset} spot` : `${asset}-PERP`;
}
export type OrderSide = 'Buy' | 'Sell';
export type OrderType = 'Market' | 'Limit';
export type TimeInForce = 'GoodTillCancelled' | 'ImmediateOrCancel' | 'PostOnly';
export type OrderStatus = 'Received' | 'Accepted' | 'Working' | 'PartlyFilled' | 'Filled' | 'Cancelled' | 'Rejected';
export type HaltScope = 'Global' | 'Venue' | 'Account';

export interface TradingPosition {
  asset: string;
  type: string;
  quantity: number;
  averagePrice: number;
  markPrice: number | null;
  notionalUsd: number | null;
  unrealisedPnlUsd: number | null;
  realisedPnlUsd: number;
  feesUsd: number;
  /** For a leveraged position on a real account: where the venue would close it by force, and how far away that is. */
  liquidationPrice: number | null;
  liquidationDistance: number | null;
}

export interface TradingAccount {
  /** What the account's perpetuals are margined in: USDT, or USDC where the venue has those too. */
  perpQuote: 'USDT' | 'USDC';
  id: number;
  managerId: number;
  manager: string;
  sleeve: string;
  venue: TradingVenue;
  venueName: string;
  label: string;
  mode: 'Paper' | 'Live';
  hasKey: boolean;
  keyHint: string | null;
  leverage: number;
  marginMode: 'Cross' | 'Isolated';
  maxOrderNotionalUsd: number;
  maxPositionNotionalUsd: number;
  priceBandBps: number;
  paperBalanceUsd: number;
  equityUsd: number;
  isHalted: boolean;
  positions: TradingPosition[];
  canGoLive: boolean;
  /** For a real account: when its venue account was last read, and why the last reading failed if it did. */
  liveReadAt: string | null;
  liveReadError: string | null;
}

export interface Halt {
  id: number;
  scope: HaltScope;
  venue: TradingVenue | null;
  tradingAccountId: number | null;
  where: string;
  reason: string;
  pulledBy: string;
  pulledAt: string;
  releaseRequested: boolean;
}

export interface Approval {
  id: number;
  kind: 'AddTradingKey' | 'RaiseLimits' | 'ReleaseHalt' | 'GoLive';
  summary: string;
  requestedBy: string;
  requestedAt: string;
  status: string;
  canDecide: boolean;
  canCancel: boolean;
}

export interface VenueStatus {
  venue: TradingVenue;
  connected: boolean;
  books: number;
  roundTripMs: number | null;
  lastMessageAt: string | null;
}

export interface EngineStatus {
  at: string;
  isLeader: boolean;
  roundMs: number | null;
  venues: VenueStatus[];
  /** How many instruments have a live book right now. */
  openInstruments: number | null;
  /** How many may be open only because somebody asked for them. */
  mostOnDemand: number | null;
}

/** An instrument by name. */
export interface InstrumentRef {
  asset: string;
  type: TradedType;
}

/** What one venue's list says about an instrument: whether it takes orders, and its smallest steps. */
export interface VenueRules {
  venue: TradingVenue;
  quote: string;
  tradable: boolean;
  priceStep: number | null;
  quantityStep: number | null;
  minQuantity: number | null;
}

export interface Trading {
  fundId: number;
  fundName: string;
  isDemo: boolean;
  canTrade: boolean;
  canAdmin: boolean;
  vaultReady: boolean;
  accounts: TradingAccount[];
  halts: Halt[];
  approvals: Approval[];
  venues: { venue: TradingVenue; name: string; needsPassphrase: boolean }[];
  managers: { id: number; name: string; sleeve: string }[];
  instruments: LiveInstrument[];
  engine: EngineStatus | null;
  /** Instruments this fund keeps at hand; their books stay open. */
  pinned: InstrumentRef[];
  /** Instruments last traded on this fund's accounts, the latest first. */
  recent: InstrumentRef[];
  /** Whether what the paper accounts hold counts in the fund's holdings, exposure, risk and NAV. */
  paperInBook: boolean;
}

export interface OrderEvent {
  at: string;
  kind: string;
  venue: TradingVenue | null;
  quantity: number | null;
  price: number | null;
  feeUsd: number | null;
  detail: string | null;
}

export interface Order {
  id: number;
  clientOrderId: string;
  tradingAccountId: number;
  account: string;
  venue: TradingVenue;
  asset: string;
  instrumentType: TradedType;
  side: OrderSide;
  type: OrderType;
  timeInForce: TimeInForce;
  quantity: number;
  limitPrice: number | null;
  reduceOnly: boolean;
  status: OrderStatus;
  filledQuantity: number;
  averageFillPrice: number;
  feesUsd: number;
  arrivalPrice: number | null;
  slippageBps: number | null;
  rejectReason: string | null;
  cancelRequested: boolean;
  source: 'Ticket' | 'Strategy';
  submittedBy: string;
  createdAt: string;
  updatedAt: string;
  events: OrderEvent[];
}

export interface BookLevel {
  price: number;
  quantity: number;
}

export interface BookVenue {
  venue: TradingVenue;
  venueName: string;
  bestBid: number | null;
  bestAsk: number | null;
  bidSize: number | null;
  askSize: number | null;
  ageMs: number;
  /** What this venue's prices are in: USDT for most, USD or USDC for some. */
  quote: string;
  bids: BookLevel[];
  asks: BookLevel[];
}

export interface Book {
  asset: string;
  bestBid: number | null;
  bestBidVenue: TradingVenue | null;
  bestAsk: number | null;
  bestAskVenue: TradingVenue | null;
  mid: number | null;
  spreadBps: number | null;
  venues: BookVenue[];
}

export interface AccountForm {
  managerId: number | null;
  venue: TradingVenue;
  label: string;
  leverage: number;
  marginMode: 'Cross' | 'Isolated';
  maxOrderNotionalUsd: number;
  maxPositionNotionalUsd: number;
  priceBandBps: number;
  paperBalanceUsd: number;
  /** What the account's perpetuals are margined in. */
  perpQuote: 'USDT' | 'USDC';
}

export const TIF_LABEL: Record<TimeInForce, string> = { GoodTillCancelled: 'Rests until cancelled', ImmediateOrCancel: 'Fill now, cancel the rest', PostOnly: 'Maker only' };
export const STATUS_LABEL: Record<OrderStatus, string> = { Received: 'Received', Accepted: 'Accepted', Working: 'Working', PartlyFilled: 'Partly filled', Filled: 'Filled', Cancelled: 'Cancelled', Rejected: 'Rejected' };
export const STATUS_COLOR: Record<OrderStatus, string> = { Received: 'default', Accepted: 'blue', Working: 'blue', PartlyFilled: 'gold', Filled: 'green', Cancelled: 'default', Rejected: 'red' };
export const APPROVAL_LABEL: Record<Approval['kind'], string> = { AddTradingKey: 'Trading key', RaiseLimits: 'Higher limits', ReleaseHalt: 'Release a halt', GoLive: 'Real money' };
