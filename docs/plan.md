# Katalepsis FT — multi-manager portfolio monitor, standalone system (DRAFT, decisions recorded, awaiting approval)

Goal: a separate product (own repository, own Render resources, no runtime link to Katalepsis) that gives an allocator visibility over a master fund split into sleeves, each run by several managers, across spot, perps, options and traditional instruments. Monitoring and reporting first; portfolio construction and execution are out of scope for this version.

Nothing in Katalepsis changes. Code is copied, not referenced.

This is a planning document. The decisions in section 12 are answered; no code is written until the plan as a whole is approved and the new repository exists.

## 1. What the user must be able to do

| # | Requirement | Where it lands |
|---|---|---|
| R1 | Top-level aggregated holdings and returns across the sleeves | Fund page |
| R2 | Gross and net asset-level holdings and performance, split spot / perp / option | Fund page, instrument-type switch |
| R3 | Drill into a sleeve: managers inside it, their positions and performance | Sleeve page |
| R4 | Inside a sleeve, asset-level exposure: are managers in the same token, how the token performed, its share of the sleeve and of the fund | Sleeve page, Assets tab + overlap matrix |
| R5 | Drill into a manager: holdings and performance | Manager page |
| R6 | Click an asset anywhere and loop back up: where it sits across all sleeves and managers | Asset page |
| R7 | All of the above per instrument type; fund performance spot vs perps vs options | Instrument-type filter on every page + attribution table |
| R8 | Performance at asset, manager, sleeve and fund level against a chosen benchmark (BTC, CoinDesk 20, …) | Benchmark selector on every performance chart |
| R9 | Traditional instruments in the mix: equities such as Coinbase stock. CME futures were dropped by the owner on 22 Nov 2026 | Instrument model, section 3 |
| R10 | Built-in example: a liquid master fund with three sleeves — Beta, Market Neutral, Directional — several managers each, unique and shared tokens | Seeded demo fund, section 6 |
| R11 | Per sleeve and aggregated: alerts, actions, risk matrix, volatility, liquidity analysis | Risk page, Alerts page, sections 5 and 7 |

## 2. Shape of the system

- **One web service**: ASP.NET Core 8 API that also serves the Angular SPA (same stack as Katalepsis, so copied code needs no translation).
- **One Postgres database** (Render Postgres), via Npgsql and EF Core. New migrations; nothing from the MySQL migrations carries over.
- **One always-on streaming worker** (Render background worker): holds the venue WebSocket connections, polls venues and chains that have no stream, writes the latest positions and prices to the cache, and publishes changes.
- **One Key Value cache** (Render Key Value): latest positions and prices, plus pub/sub from the worker to the web service. The web service pushes updates to open browsers over SignalR.
- **One scheduled job** (Render cron job running the same image with a `snapshot` argument): writes the end-of-day snapshot used for performance history.
- **No SQS, no AWS, no separate auth server.** Sign-in is built-in accounts with Admin and Viewer roles (ASP.NET Identity) inside the web service.
- **One organisation, several funds.** No tenant key on the tables.
- Deployed from a `render.yaml` blueprint in the new repository, on paid always-on instances of the smallest size; exact instance types are confirmed against the Render workspace before anything is created.

Layers follow the Katalepsis convention (Domain → Persistence + Infrastructure → Application with MediatR → Rest + Worker), in a small solution of about six projects plus tests.

### Venues

Read-only connections. CeFi venues use read-only API keys per manager account; DeFi venues are read from the manager's wallet address, so no keys.

| Venue | Type | Client in Katalepsis to copy |
|---|---|---|
| Binance | CeFi | yes (spot + USD-M perps) |
| Bybit | CeFi | yes |
| OKX | CeFi | yes |
| Deribit | CeFi | yes (options, Greeks) |
| Coinbase (Prime + Exchange) | CeFi | yes |
| KuCoin | CeFi | yes |
| MEXC | CeFi | yes |
| Bitget | CeFi | no — new |
| Gate | CeFi | no — new |
| Kraken | CeFi | no — new (Katalepsis has only the enum value) |
| Hyperliquid | DeFi | no — new |
| dYdX | DeFi | no — new |
| GMX | DeFi | no — new |
| Uniswap | DeFi | swap code only; position reading is new |
| Aerodrome | DeFi | swap code only; position reading is new |

Each venue is one adapter behind a single read-only interface: balances, positions (spot / perp / future / option / liquidity pool), mark prices, and a stream where the venue offers one. The Katalepsis interfaces are trading-oriented (place and cancel orders); only their read methods are carried over.

Liquidity-pool positions (Uniswap, Aerodrome) are broken down into their two underlying token amounts so they roll up on the asset page like spot.

API keys are stored encrypted with ASP.NET Data Protection, the key ring protected by a secret held in Render. This replaces the AWS KMS encryption Katalepsis uses. The system refuses to save a key that has trading or withdrawal permission where the venue lets us check.

## 3. Domain model

Hierarchy:

```
Fund (master) → Sleeve → Manager account → Position → Instrument → Asset
```

- **Fund**: name, base currency, default benchmark.
- **Sleeve**: name, mandate type (Beta / Market Neutral / Directional / other), mandate limits (for example net exposure band, max single-asset weight), benchmark override.
- **Manager account**: manager name, sleeve, inception date, allocated capital, data source (demo / upload / venue connection).
- **Asset**: the thing exposure is measured in (BTC, ETH, COIN, …). This is the key that the loop-back view (R6) groups on.
- **Instrument**: what is actually held. Type: Spot, Perp, Future (dated), Option, Equity, Cash. Fields: underlying asset, venue, multiplier, expiry, strike, call/put, quote currency.
  - Coinbase stock is an Equity instrument whose asset is COIN.
  - A dated future is a Future instrument whose underlying asset is the asset it settles on, so it rolls up with that asset's spot and perps on the asset page. (CME futures themselves are out of scope.)
- **Position snapshot** (one row per manager, instrument, day): quantity, price, market value, notional, delta, delta-adjusted exposure, unrealised PnL, entry price.
- **Cash flow**: subscriptions, redemptions and transfers per manager account, needed for time-weighted returns.
- **NAV snapshot**: per manager per day; sleeve and fund NAV are sums.
- **Price bar**: daily close and volume per asset or instrument, per source.
- **Benchmark**: either a single asset, a weighted basket, or an uploaded series.
- **Alert rule**, **alert**, **action** (section 7).

Two numbers per position, kept separate everywhere:

- **NAV contribution**: spot = quantity × price; perp or future = unrealised PnL (plus posted margin as cash); option = quantity × mark.
- **Exposure**: spot = market value; perp or future = quantity × price × multiplier; option = delta × quantity × underlying price.

Katalepsis already values this way for NAV (`Horizen.Ria/Execution/AccountValuation.cs:98` adds perp PnL to NAV while line 107 reports notional). Long, short, gross and net are sums over the exposure number.

## 4. What is copied from Katalepsis

Read in this session:

| Piece | Source | Use |
|---|---|---|
| Return, volatility, Sharpe, Sortino, drawdown, Calmar from a NAV series | `Horizen.Ria/Backtests/BacktestMetricsCalculator.cs:17-59`, drawdown series `:62-73`, chart downsampling `:89-118` | Performance metrics at every level |
| Realised volatility, return matrix, covariance, Ledoit-Wolf shrinkage | `Horizen.Ria/Engine/Common/Indicators.cs:13-47`, `:97-139`, `:142` onward | Volatility, correlation, portfolio risk |
| Valuation of spot + perp + option holdings into lines with weights | `Horizen.Ria/Execution/AccountValuation.cs:33-151` | Starting point for the position valuer; extended with futures, equities and delta |
| Daily return with deposits and withdrawals, chain-linked | `Application/Services/Balance/BalanceHistoryService.cs:103-119` and `:180-202` | The formula only (see note) |
| Frontend stack versions | `Rest/ClientApp/package.json:16-46` (Angular 21.2, ng-zorro 21.3, NGXS 21, Chart.js 4.4) | Same versions in the new app |

Note on `BalanceHistoryService`: the formula is right, but two details should not be copied. Lines 121-125 repeat the previous day's PnL when the opening balance is zero, and line 184 truncates each day's percentage to two decimals before line 200 compounds it, so the cumulative index drifts. The new service will compound unrounded returns and round only for display.

From the inventory, to be re-read before each is copied (not yet read by me line by line):

| Piece | Source |
|---|---|
| Chart wrapper with USD / percent / multiple axes | `Rest/ClientApp/src/app/ria/shared/ria-chart.component.ts` |
| Display pipes | `Rest/ClientApp/src/app/common/pipes/enumToString.pipe.ts`, `dynamic-decimal.pipe.ts`, asset-logo, market-logo |
| Layout shell | `Rest/ClientApp/src/app/layouts/main-layout/` |
| Public market data (no API key) | `Horizen.Ria/Integration/MarketData/BinancePublicClient.cs`, `DeribitPublicClient.cs` |
| Strategy-vs-asset benchmark chart | `Application/Requests/Report/Queries/AssetPnlHandler.cs`, `analytics/pnl-benchmark/pnl-benchmark.component.ts` |
| Slack webhook sender | `Infrastructure.Notifications/Slack/SlackNotifier.cs:28-51` |
| Option Greeks | `Horizen.Ria` `OptionMath.cs` / `OptionPricer.cs`, `Application/Services/Options/OptionsWatcher.cs:222` |
| Venue clients (read methods only) | `Infrastructure/Services/{Binance,Bybit,Okx,Kucoin,Mexc,Deribit,Coinbase}/…` |
| WebSocket position watchers | `Application/Services/Positions/Perpetual/{Binance,Bybit,Kucoin,Okx}PerpetualPositionsWatcher.cs` |
| On-chain balance reading | `Infrastructure/Services/OnChain/OnChainService.cs` |
| VaR, CVaR, beta definitions | `Tools/StrategyResearch/research/metrics.py:36` (Python; ported to C#) |

Not copied, and why:

- **Horizen.Auth** (OpenIddict server): needs its own service, certificates and AWS Secrets Manager. Built-in accounts were chosen instead (D3).
- **Redis position cache code**: it relies on the RedisJSON module, which is not confirmed on Render Key Value. The cache is written new with plain string values.
- **SQS alarm pipeline**: replaced by evaluating rules in the worker as positions change.
- **KMS wallet encryption**: replaced by ASP.NET Data Protection (section 2).
- **Order placement, bots, fix-balance, TradeAnalytics**: execution, out of scope.

Not available in Katalepsis at all, so written new:

- The fund → sleeve → manager hierarchy (Katalepsis is flat: Strategy → Wallets).
- Gross / net / long / short exposure aggregation.
- Configurable alert rules (Katalepsis thresholds are constants).
- Liquidity analysis beyond a ±2% depth snapshot.
- Anything for traditional instruments.

## 5. Analytics

**Performance** (asset, manager, sleeve, fund; each also filtered by instrument type):

- Time-weighted return: daily, MTD, QTD, YTD, since inception; cumulative curve.
- PnL contribution: by asset, by manager, by sleeve, by instrument type (answers "spot vs perps vs options").
- Against the selected benchmark: excess return, beta, correlation, tracking error, information ratio, up/down capture.
- Volatility, Sharpe, Sortino, max drawdown, Calmar.

**Exposure**:

- Long, short, gross, net in USD and as a share of NAV, at every level.
- Weight of each asset in the manager, in the sleeve and in the fund (R4).
- Manager overlap matrix per sleeve and across the fund: which managers hold the same asset, in which direction, and whether they offset.

**Risk matrix** (per sleeve and aggregated):

- Scenario grid: market shocks (for example BTC −30 % to +30 %) against volatility or alt-beta shocks, each cell showing the PnL impact. Positions are repriced through beta to BTC for linear instruments and delta/gamma/vega for options.
- Mandate check per sleeve: for example Market Neutral net exposure and beta inside the band; Beta sleeve tracking error to its benchmark.
- VaR and CVaR at 95 % (historical), concentration (top 5 / top 10 share), leverage (gross / NAV), margin usage and distance to liquidation for perps where the source provides it.

**Volatility**: realised 30/90-day per asset, manager, sleeve, fund; volatility contribution by asset and by manager; correlation heat map.

**Liquidity**:

- Per position: size against average daily volume, days to liquidate at a set participation rate.
- Per manager, sleeve, fund: liquidity ladder (share of NAV liquidated within 1, 5, 20 days), illiquid tail list.

## 6. Built-in example fund

Seeded on first start; deterministic, so screenshots and tests are stable.

| Sleeve | Managers | Character |
|---|---|---|
| Beta | 2 | Long-only large caps, close to a market-cap basket; one holds COIN stock |
| Market Neutral | 3 | Spot long against perp short (basis), funding capture, an options volatility book; net exposure near zero |
| Directional | 3–4 | Concentrated long/short, mid-cap tokens, some options; several tokens shared between managers and with the Beta sleeve, a few unique |

Positions are synthetic. Prices and history are real (public market data), so returns, volatility, benchmarks and liquidity numbers are real figures for the invented positions. Shared tokens are chosen on purpose so the overlap matrix and the asset loop-back page have something to show, including one asset that one manager is long and another is short.

## 7. Alerts and actions

- **Alert rule**: scope (fund / sleeve / manager / asset), metric, operator, threshold, severity, channel. Editable in the UI.
- **Seeded rules**: drawdown beyond X %, daily loss beyond X %, single-asset weight above X % of sleeve or fund, sleeve net exposure outside its mandate band, gross leverage above X, liquidity (days to liquidate above X for more than Y % of NAV), crowding (N managers in the same asset above X % of fund), stale or missing data from a manager, missing price.
- **Evaluation**: in the worker as positions and prices change, throttled so a rule is checked at most once per short interval. An open alert is not re-raised while the breach continues; it closes when the metric recovers.
- **Actions**: on each alert — acknowledge, assign, comment, snooze, resolve, with an audit trail. This is workflow on the monitoring side, not trading (decision D5).
- **Delivery**: in-app inbox; Slack webhook and email as optional channels.

## 8. Screens

1. **Fund**: NAV, return vs benchmark, exposure tiles (long / short / gross / net), sleeve table, top assets, open alerts. Instrument-type switch.
2. **Sleeve**: tabs Managers, Assets, Overlap, Performance, Risk, Liquidity.
3. **Manager**: positions table, performance vs benchmark, exposure, risk.
4. **Asset**: price and performance, then every position in that asset grouped sleeve → manager → instrument, with share of sleeve and of fund. Reached by clicking any asset anywhere.
5. **Risk**: scenario matrix, VaR, volatility, correlation, with a scope selector (fund or one sleeve).
6. **Liquidity**: ladder and illiquid tail, same scope selector.
7. **Alerts**: inbox, rule editor, action history.
8. **Admin**: funds, sleeves, managers, instruments, benchmarks, position upload.

Breadcrumb on every page (Fund › Sleeve › Manager › Asset), with global selectors for date range, benchmark and instrument type that persist across navigation.

## 9. Delivery phases

Each phase ends deployed on Render and checked in the browser. Each is split into tasks of at most three files when work starts.

- [ ] **Phase 0 — Skeleton**
  - [x] Repository, nine-project solution, CI
  - [x] Dockerfile and `render.yaml`; web (2 instances), three workers, Postgres and Key Value live on Render in Frankfurt
  - [x] Health and readiness endpoints; worker heartbeats over Key Value pub/sub
  - [x] Database migrations run in the pre-deploy step
  - [x] Built-in sign-in (Admin, Viewer), session keys stored in Postgres so both web instances accept a session
  - [x] Angular app shell served by the web service: sign-in, change password, layout, system status page
- [ ] **Phase 1 — Model and data**
  - [x] Fund, sleeve, manager, instrument, position, price and benchmark model with migration
  - [x] Performance and statistics calculators copied from Katalepsis with their tests
  - [x] Position valuer (spot, equity, cash, perp, future, option) and exposure summary
  - [x] Daily crypto prices from Binance public market data (client copied from Katalepsis)
  - [x] Demo fund: three sleeves, nine managers, spot and perp books on real prices, ONDO held by five managers; rolled forward on every deploy
  - [ ] Options in the demo fund (needs the option pricer from Katalepsis)
  - [ ] Equities in the demo fund (needs the free equity price source to be chosen; CME futures are out of scope)
  - [x] Daily scheduled job at 00:20 UTC, so the demo fund and prices advance without a deploy
  - [ ] Key Value internal authentication: a switch in the Render dashboard, not available through the blueprint or API
- [x] **Phase 2 — Holdings and drill-down**
  - [x] Holdings API: fund list, overview, holdings by asset, sleeve, manager, asset loop-back; instrument-type filter
  - [x] Screens: fund overview and holdings, sleeve (managers, assets, overlap, mandate checks on positions), manager positions, asset across the fund
  - Not in this phase: returns and benchmark (Phase 3), risk and liquidity (Phase 4), alerts (Phase 5)
- [ ] **Phase 3 — Performance and benchmarks**
  - [x] Time-weighted returns with cash flows; volatility, Sharpe, Sortino, drawdown; beta, correlation, tracking error, information ratio
  - [x] Performance API and tabs for fund, sleeve and manager: cumulative chart against the benchmark, drawdown, period and monthly returns, children against the benchmark, profit split by sleeve or manager, instrument type and asset
  - [x] Asset price return against the benchmark on the asset page
  - [x] Benchmarks: BTC, ETH, cash; period and benchmark selectors kept across pages; chart component copied from Katalepsis
  - [ ] CoinDesk 20 approximating basket (needs the index's current constituents and weights)
- [ ] **Phase 4 — Risk, volatility, liquidity**
  - [x] Scenario matrix (BTC move × how strongly other assets follow, by measured beta), also per sleeve or manager
  - [x] Historical value at risk and expected shortfall (95%, today's book over the last 365 days of price moves), leverage, concentration
  - [x] Mandate limits checked per sleeve: gross and net exposure, largest asset, deepest manager drawdown, days to liquidate
  - [x] Volatility (30 and 90 days, rolling 30-day chart, share of variance) and manager correlation matrix
  - [x] Liquidity: days to liquidate per asset at a chosen share of daily volume, ladder for the scope and each child, who holds the slowest asset
  - [x] Risk and Liquidity tabs on fund, sleeve and manager
  - [ ] Funding income on perps is not modelled in the demo fund, so basis and funding managers show zero volatility
- [ ] **Phase 5 — Alerts and actions**
  - [x] Rules on a fund, a sleeve or a manager: figure, above or below, threshold, severity, on/off. One rule per mandate limit is kept in step with the limit; custom rules are free to edit
  - [x] Evaluation after each daily close, on every rule change and on demand: one alert per breach, refreshed while it lasts, closed as recovered when it ends; state in Postgres
  - [x] Workflow (Admin): acknowledge, assign, comment, snooze, resolve, with the full history on each alert. An alert resolved by hand is not raised again until the figure has recovered and breached anew
  - [x] Alerts tab with inbox (whole fund or one sleeve; active, resolved, all) and rule editor; alert strip on the fund and sleeve pages
  - [ ] Delivery to Slack and email (needs a Slack webhook and a mail provider)
  - [ ] Rules on a single asset across the fund (for example, number of managers holding it)
  - [ ] Evaluation moves into the core worker when live positions arrive (Phase 6)
- [ ] **Phase 6 — Live connection framework + first venues**
  - [x] Real funds: an Admin creates a fund with its sleeves and managers; funds are listed in the navigation
  - [x] Read-only adapter contract (check a key, read a whole account; nothing that trades), one normalised position shape, adapters registered by venue
  - [x] Venue keys encrypted with the MASTER_KEY secret (AES-256-GCM); a key is checked with the venue before it is stored and refused if it can trade or withdraw
  - [x] CeFi worker reads every connection about once a minute, with back-off on failure; a failed read keeps the last complete book and records why
  - [x] A manager's book is the sum of its connections and feeds the existing screens (holdings, performance, risk, liquidity, alerts) through today's snapshot
  - [x] Connections tab: state, last read, and the account value computed from positions next to the value the venue itself states
  - [x] Deribit (spot balances, perps, dated futures, options; inverse and USDC-settled) and OKX (trading account: balances, swaps, futures, options)
  - [ ] Field readings of all four venues are tested on hand-written answers; Deribit and OKX also on the real venues' refusal of a bad key; a real read-only key has not been tried yet
  - [x] Binance (spot wallet incl. flexible savings, USDⓈ-M futures balances and positions) and Bybit (unified account: balances, linear and inverse perps and futures, options)
  - [ ] Binance and Bybit have never been called for real: both block the build environment, so even the refusal of a bad key is untested until tried from Render
  - [ ] Binance: coin-margined futures, margin and options accounts, funding wallet; a holding with no USD pair fails the read. Bybit: funding account, classic accounts
  - [x] Live push to the browser: after every read the worker announces "fund changed" over Key Value, each web instance tells its own browsers over one WebSocket, and open pages reload their figures. Without a socket, pages refresh every minute and the header says so
  - [x] Five-minute samples of each manager's NAV and exposure, kept 90 days; the fund's last 24 hours are charted on its overview
  - [ ] Venue streams instead of one-minute polling, and the live book in Key Value (figures are read from Postgres today)
  - [x] Deposits, withdrawals and transfers read from all four venues every ten minutes and recorded as cash flows, so they are not counted as performance; a venue that will not show them is flagged on the connection
  - [ ] Transfer readings are tested on hand-written answers only. Known limits: transfers before the connection was added are ignored; OKX and Bybit show seven days back; a Deribit key needs the wallet:read scope
  - [x] Alert rules are checked after every read of a connection
  - [x] Structure tab: rename a fund and set its benchmark; add, rename and remove sleeves; set each sleeve's mandate limits; add, change and remove managers. A manager with history or venue keys cannot be removed; the example fund is read-only
  - [ ] OKX funding account
- [ ] **Phase 7 — Remaining copied CeFi venues**
  - [x] KuCoin: all spot-side accounts, USDT- and BTC-margined futures, deposits and withdrawals; key permissions are read and a key that can trade or move money is refused
  - [x] MEXC: spot wallet, USDT/USDC futures where MEXC opens its futures API to the account, deposits and withdrawals
  - [x] Coinbase Exchange: balances valued at Coinbase's public USD rates, transfers
  - [x] Coinbase Prime: every portfolio the key can see, at the USD value Prime states, deposits and withdrawals
  - [x] Parts of an account that could not be read are stated on the connection instead of being left out silently
  - [ ] MEXC and both Coinbase venues do not say what a key may do, so their keys are stored as "permissions unverified": create them read-only at the venue
  - [ ] All four are tested on hand-written answers and on the real venues' refusal of a bad key; no real account has been read
- [ ] **Phase 8 — New CeFi venues**
  - [x] Kraken: spot balances (staked and lent forms count as the coin), deposits and withdrawals from the ledger
  - [x] Gate: spot account, USDT-settled futures, deposits and withdrawals
  - [x] Bitget: spot account, USDT-margined futures, deposits and withdrawals
  - [x] Written as small signed REST clients that can only send GET requests (Kraken's read calls are POST by its own design); each real venue was seen to refuse a made-up key
  - [ ] None of the three says what a key may do, so their keys are stored as "permissions unverified"
  - [ ] Not read: Kraken Futures (a separate system with its own keys); Gate margin, options and delivery futures; Bitget coin-margined and USDC futures. Field readings are tested on hand-written answers only
- [ ] **Phase 9 — DeFi venues**
  - [x] Connections by wallet address: no secret, nothing can be traded through them; read by the DeFi worker
  - [x] Hyperliquid: perp account, spot balances, deposits, withdrawals and transfers. Checked against two real public accounts with 177 open positions each: the value computed from the positions equals the value Hyperliquid states
  - [ ] Hyperliquid: money placed in vaults and staked HYPE are not read; contracts quoted per thousand coins (kPEPE) keep their own name
  - [x] The core worker loads price history for an asset within five minutes of a connection first bringing it in, and keeps every asset's history current
  - [ ] An asset Binance does not list has no price history at all, so its beta is assumed and its volume unknown
  - [x] Wallets by address on the top chains: one address read on Ethereum, BNB Chain, Arbitrum, Base, Optimism, Polygon and Avalanche at once; Solana; Bitcoin; Tron. Each was read against a real public address
  - [x] Every token contract in the list was checked on its chain for its symbol and decimals; balances are valued at Binance's public prices
  - [ ] Only each chain's native coin and its main tokens are read (36 contracts on the EVM chains, 5 on Solana, USDT on Tron). Finding every token of an address, staked coins and positions inside protocols need an indexing provider
  - [ ] Transfers in and out of an on-chain wallet are not read (the same provider would supply them), so they show as profit or loss; the connection says so
  - [ ] Solana is read from its free public endpoint, which limits how often it answers; a paid endpoint would be steadier
  - [x] Uniswap v3 pool positions on Ethereum, Arbitrum, Optimism, Polygon, Base and BNB Chain, and Aerodrome concentrated-pool positions on Base, read with the EVM wallet and counted as the tokens inside them. The arithmetic was checked against what the Uniswap contract itself returns for a real position
  - [ ] Pool positions: only those held directly by the wallet and made of the listed main tokens. Not read: Aerodrome positions staked in a gauge (which is where most of them are), Aerodrome's basic pools, Uniswap v2 and v4, and fees earned but not collected
  - [ ] dYdX (its API refuses address lookups from the build environment), GMX
- [ ] **Phase 10 — Traditional positions**
  - [x] Positions entered by hand on any manager of a real fund (equity, spot, perp, future, option, cash), added to whatever its keys and wallets bring in
  - [x] CSV upload that replaces the manager's manual book; any faulty line refuses the whole file and is named, so a half-loaded book is never valued; downloadable template
  - [x] A position without a price takes the asset's latest daily close; an asset with no price feed (every equity today) needs a price entered by hand
  - [x] Cash flows entered by hand, shown next to those read from venues
  - [x] Books made only of manual positions are revalued every five minutes by the core worker, so prices move and each day gets its record
  - [x] Price feed for listed shares from Yahoo Finance's public chart data (free, no key, unofficial): daily closes and traded volume, loaded the moment a share is first entered and kept current by the core worker. A share can now be entered without a price
  - [ ] Share prices are treated as USD, which is right for US listings only; the feed is daily closes, not live prices
  - [ ] XLSX upload (CSV only today)

Phases 0–5 run on the demo fund, so the full drill-down, performance, risk and alert experience is usable before the first live venue is connected. Phases 6–10 each add venues without changing the screens.

Tests per phase: xUnit for calculators and handlers (returns, exposure, aggregation, alert evaluation), integration tests on the API with a Postgres test database, Playwright for the drill-down and loop-back paths.

## 10. Edge cases the design has to handle

- A manager is long an asset and another is short it: net is small, gross is large; both must show, and crowding rules use gross.
- Perps and futures: weight by notional can exceed 100 % of NAV; percentages are labelled as exposure share, not NAV share.
- Options: exposure is delta-adjusted and changes with price; short options need gamma in the scenario matrix or the loss is understated.
- Subscriptions and redemptions on the same day as a large move; a manager starting mid-period; a manager with zero NAV at the start of a day.
- Transfers between managers or sleeves must not appear as performance at fund level.
- Traditional markets close on weekends and holidays while crypto does not: last close is carried forward and marked as stale; annualisation uses 365 days for the mixed portfolio.
- Positions quoted in a non-USD currency.
- A missing price: the position is listed as unpriced and raises an alert rather than being valued at zero silently.
- Managers reporting at different times or late: each manager shows its "as of" time; fund totals show the oldest one.
- Stablecoins and cash: counted in NAV, excluded from directional exposure.
- The same asset under different tickers on different venues: one asset record with venue symbol mappings.
- Benchmark history shorter than the portfolio history.
- Expired options and rolled futures leaving the book between snapshots.
- A venue stream drops or a venue is down: the manager's book keeps its last values, is marked stale with its age, and a stale-data alert fires; fund totals do not silently fall.
- A venue rate-limits or bans the key: back-off per venue, never a tight reconnect loop.
- The worker restarts: every book is rebuilt from a full venue read before streaming resumes, so a missed update cannot leave a wrong position on screen.
- An API key that turns out to have trading or withdrawal rights, or that expires or is revoked.
- One manager with accounts on several venues, and sub-accounts on one venue.
- The same asset held on a CeFi venue and on-chain, and wrapped or staked forms of an asset (WETH, stETH) that should roll up with it.
- Liquidity-pool positions whose token amounts change with price.
- Venue-reported and locally computed values disagreeing (mark price, unrealised PnL): the venue's figure is shown and the difference is logged.

## 11. Facts not yet verified

- Which free live source to use for equity prices, and what its terms, delay and rate limits are. Free feeds are often delayed by about 15 minutes; the screen will show the price time. To be chosen and checked in Phase 1.
- The current constituents and weighting rule of CoinDesk 20, needed to build the approximating basket.
- What each of the three new CeFi venues and five DeFi venues exposes for read-only positions and streams; checked at the start of its phase.
- Whether each venue lets us read a key's permissions, for the read-only check.
- Render instance types and limits for the web service, worker, Postgres, Key Value and cron job; checked in Phase 0 against the Render workspace.
- Everything in the second table of section 4.

## 12. Decisions (answered)

| # | Decision | Answer |
|---|---|---|
| D1 | Stack | Same as Katalepsis: .NET 8 + Angular 21 + ng-zorro + Chart.js, on Postgres |
| D2 | Repository | The owner creates a new repository and grants access; the plan moves there |
| D3 | Sign-in | Built-in accounts with Admin and Viewer roles |
| D4 | Position data | Live read-only venue connections, plus the built-in demo fund |
| D4a | CeFi venues (10) | Binance, Bybit, OKX, Deribit, Coinbase (Prime + Exchange), KuCoin, MEXC, Bitget, Gate, Kraken |
| D4b | DeFi venues (5) | Hyperliquid, dYdX, GMX, Uniswap, Aerodrome |
| D4c | Traditional instruments | Prices from a free live data source; positions by manual entry and CSV/XLSX upload |
| D4d | CoinDesk 20 benchmark | An approximating basket of its constituents, labelled as an approximation |
| D5 | Actions | Workflow on alerts: acknowledge, assign, comment, snooze, resolve. No instructions to managers, no trading |
| D6 | Refresh | Real time: venue streams pushed to the screen, plus an end-of-day snapshot |
| D7 | Tenancy | One organisation, several funds |
| D8 | Render budget | Paid, smallest always-on tiers |
| D9 | Product name | Katalepsis FT |

Still needed from the owner before Phase 0: the new repository (organisation and name) with access granted, and approval of this plan.
