# Parity with Katalepsis: portfolios, execution and tradable assets

This is the plan to bring Katalepsis Fund Terminal level with Katalepsis in three areas: what can be traded, how
orders are worked, and how a portfolio is steered towards a target. It is a plan only; nothing in it is built yet.

Left out on purpose, as agreed: the eight Katalepsis bots and the listing-announcement bot, DEX swaps and Uniswap
liquidity management, and the client portal. Outside strategies keep reaching the system through the strategy API.

Screens for the main parts are drawn in `docs/mockups/`.

## Principles

1. **One way to trade, not three.** Katalepsis has "fix balance operations", a separate RIA module with its own
   executor, and bot-specific order code. Here every order, whether one click, a slice of a larger order or a line
   of a rebalance, goes through the same checks, the same engine and the same record.
2. **Copy what is proven, redesign what is tangled.** Venue knowledge (symbols, steps, error codes, option and
   conditional-order calls) is copied from Katalepsis with its tests. Flow and data model are designed here.
3. **Every phase works on paper first.** Paper fills run against live books, as today. A phase is finished when it
   works on paper and its real-order path is tested against recorded venue answers.
4. **Nothing weakens the controls.** Limits, price bands, the kill switch and two-person approval apply to every
   new order kind from the day it exists.

## The phases at a glance

| # | Phase | What the user gets | Size |
|---|---|---|---|
| 1 | Any asset | Search and trade every pair a venue lists, not ten fixed assets | Medium |
| 2 | One book | Positions on trading accounts appear in holdings, performance and risk | Small |
| 3 | Working orders | Large orders sliced over time, baskets, approval above a set size | Large |
| 4 | Targets and rebalancing | Target weights, a drift view, one-click rebalance proposals | Large |
| 5 | Perp protection | Take-profit and stop-loss, fill-or-kill, margin alerts by email and Slack | Medium |
| 6 | Options | Trade BTC and ETH options on Deribit | Medium |
| 7 | Venues | Bitstamp, Coinbase Prime and Hyperliquid order connections | Medium |
| 8 | Exports and custody | Holdings and trade exports, Fireblocks and Utila balances, three more chains | Small |

Order: 1 and 2 first, because everything after depends on them. Then 3, then 4 (which is built on 3). Phases 5 to 8
do not depend on each other and can be taken in any order after 3.

---

## Phase 1 — Any asset

**Today.** The execution worker opens books for ten assets named in a setting. Katalepsis asks each venue for its
symbols every time a screen needs them.

**Better.** One instrument catalogue, kept in the database and refreshed from every venue each hour: venue, asset,
kind (spot or perp), quote currency, price step, quantity step, smallest order, and whether the venue is accepting
orders for it. The ticket searches the catalogue. Books are opened on demand: when someone looks at an instrument or
an order is working in it, and closed a few minutes after the last interest. Each fund keeps a short pinned list
that is always open.

- Quote currencies: USDT, USDC and USD. USDC-margined perps on Binance and Bybit are added.
- Asset names differ between venues (XBT and BTC, 1000PEPE and PEPE). The catalogue holds the venue's own name and
  the standard one.
- An instrument with no fresh book cannot be traded; the ticket says so.

**Copied from Katalepsis.** `Application/Services/MarketInfo/MarketInfoService.cs` and `PerpetualSymbolsService.cs`
(how each venue lists symbols and which to keep), `Application/Services/Portfolio/AssetMatchService.cs` (name
mapping), the KuCoin and OKX perp symbol rules.

**New here.** Table `instruments`; a refresh job in the `core` worker; book subscriptions with a count of who needs
them; an upper limit on open books per venue.

**Done when.** A search for any listed pair finds it, its book opens within a few seconds, and a paper order fills.
The ten-asset setting is gone.

**Status (25 November 2026): built.** The list is read hourly from 20 venue markets into `listed_instruments`,
with each venue's price step, quantity step and smallest order where its list gives them. The ticket searches it. A
book opens when an instrument is read and closes five minutes after the last read, unless an order is working or a
position is held in it, or a fund has pinned it. The ten assets of `EXEC_ASSETS` stay open always, and
`EXEC_MAX_BOOKS` (default 80) caps how many more may be open on demand. A fund can pin up to twelve instruments; the
ticket also shows what was traded lately and how many instruments are open. Hyperliquid's thousand-names (kPEPE) are
listed under the name other venues use (1000PEPE).

USDC-margined perpetuals on Binance and Bybit have their own books and lists, and a trading account can be set to
trade them instead of the USDT ones (a manager who wants both has two accounts on the venue). They trade on paper.
**Going live on a USDC-margined account is refused**: the two venues' real-order calls for those contracts are not
written, because they could not be tried from the development machine.

**Risk.** Venue limits on the number of streams. The cap and the idle close keep within them; the limit per venue is
measured, not guessed.

## Phase 2 — One book

**Today.** A fill on a trading account changes that account's position and nothing else. The fund's holdings come
only from read-only keys, wallets and positions entered by hand.

**Better.** A trading account becomes one more source of a manager's book. Its positions flow into holdings,
performance, risk, liquidity and alerts within the same minute as a fill.

- Where the same venue account also has a read-only key, it is counted once. The venue's account id decides.
- Paper accounts are shown but kept out of NAV, behind a switch on the fund ("include paper positions"), so a paper
  demo can show the full loop without mixing paper and real money.

**Status (25 November 2026): built.** A trading account is a third source of its manager's book, beside read-only
keys and hand-entered positions. It is counted whole, positions and cash, so its value in the book is what the
account is worth.

- A real-money account counts, unless the manager also has a read-only key on the same venue: that key is taken to
  read the same account. The rule is by venue, not by the venue's account id, which the system does not hold.
- A paper account counts only when the fund's switch "Count paper accounts in the fund's book" is on. It is off by
  default.
- An account coming into the book or leaving it is recorded as money arriving or leaving, so returns are not moved
  by it.
- Managers with no venue read to trigger a rewrite have their book rewritten every twenty seconds by the core
  worker; that is how a fill reaches holdings.

Not handled: deposits into a real-money trading account that no read-only key sees will look like performance,
because a trading key's transfers are not read.

**Copied from Katalepsis.** Nothing; this gap is ours.

**Done when.** A paper order placed on the Trading tab appears on Holdings and moves net exposure on Overview.

## Phase 3 — Working orders

**Today.** One order at a time, market or limit. Katalepsis works a holding towards a balance with random-sized
slices, a wait between them and a pause price, each one typed in by hand and approved by reviewers; its RIA module
has a separate TWAP.

**Better.** A *working order* is a parent with its own state, guards and record, and child orders that are ordinary
orders. Three styles, one engine:

| Style | What it does | Where it comes from |
|---|---|---|
| Over time (TWAP) | Even slices across a set duration | Katalepsis RIA `ProposalExecutor`, `OrderBuilder` |
| Paced | Random slice size between two bounds, a wait between slices, no end time | Katalepsis `TradeEngineService` |
| Passive | Rests at the best price as maker, re-quotes when the market moves, crosses only if told to | Katalepsis maker mode in `TradeEngineService` |

For every style:

- **Best venue for each slice.** Where a manager has accounts on several venues, each slice goes to the one with the
  best price after fees at that moment. Katalepsis has no such routing.
- **Guards.** A pause price (stop buying above, stop selling below), a largest slippage against the arrival price,
  a stop if margin runs low on a perp account, and the existing price band on every child.
- **Controls.** Pause, resume, stop. The kill switch stops parents as well as children.
- **Record.** Arrival price, average price achieved, slippage in basis points, fees, time taken, and every child.
- **Baskets.** Many lines entered or pasted at once, checked together, approved together, worked side by side.
- **Approval.** Each trading account has an amount above which an order needs a second person. One rule replaces
  the per-strategy reviewer counts in Katalepsis.
- **Restart safety.** A parent survives a restart of the execution service: it reads its children back and carries
  on from what was actually filled, under the same write-first rule as single orders.

**New here.** Tables `working_orders` and a parent link on `orders`; a step in the execution loop; the approval
kind "large order".

**Done when.** A one-hour TWAP on paper finishes within tolerance of its schedule, survives a restart half-way, and
reports its slippage; a basket of ten lines needs one approval and runs to the end.

**Risk.** A parent that keeps sending after something has gone wrong. Every parent has a hard end: a largest number
of children, a latest end time, and a stop after three child rejections in a row.

## Phase 4 — Targets and rebalancing

**Today.** Nothing. Katalepsis keeps target holdings per strategy as amounts and leaves the user to type one
operation per asset; its RIA module has model portfolios and proposals but lives apart from the rest.

**Better.** Targets belong to a manager or an SMA and are written as weights of NAV, with a tolerance for each line.

- **Versions.** A target has versions with a start date. The history shows what the target was on any day.
- **Drift view.** Each line: target, actual, difference in percent and dollars, inside or outside tolerance.
- **Proposal.** One click turns the drift into a basket: what to buy and sell, on which accounts, with the expected
  cost and the result after trading. Three kinds, as in Katalepsis RIA: rebalance, first investment, liquidation.
- **Checks before approval.** The whole basket is tested against limits and mandates as if it had already traded.
  A proposal that would breach a mandate cannot be approved.
- **Execution.** After approval the proposal becomes working orders from phase 3. Its page shows progress and the
  drift closing.
- **Alert.** A new alert measure, "largest drift from target", so drift raises an alert like any other limit.

**Copied from Katalepsis.** `Horizen.Ria` drift and order sizing (`OrderBuilder`, `ExecutionRules`), the proposal
states, and the post-trade band check from `Horizen.Ria/Execution/PreTradeChecks.cs`. The model library and
backtests are not copied.

**New here.** Tables `targets`, `target_lines`, `proposals`, `proposal_lines`; a Targets tab on manager and SMA
pages.

**Done when.** A target entered for an SMA shows drift; a proposal is approved by a second person and runs on paper
until every line is inside tolerance.

## Phase 5 — Perp protection

- **Take-profit and stop-loss** attached to a position, sent as the venue's own conditional orders so they hold
  when our service is down. Always reduce-only. Amend and cancel from the position row.
- **Fill-or-kill** as a fourth time in force, where the venue has it.
- **Margin alerts delivered.** Margin ratio and distance to liquidation become alert measures, and alerts gain
  delivery by email and Slack. This also closes the open item "alerts are on screen only".

**Copied from Katalepsis.** Conditional-order calls for Binance, OKX, Bybit and KuCoin
(`Application/Requests/Position/Commands/PerpetualOpenOrderCommand.cs` and the perp clients), fill-or-kill mapping
per venue, and `Infrastructure.Notifications/` for email and Slack.

**Done when.** A stop-loss placed on paper triggers when the book crosses it; on a real-order path it is tested
against recorded venue answers; a margin alert arrives in a Slack channel.

## Phase 6 — Options on Deribit

- An option chain for BTC and ETH: expiries across, strikes down, bid, ask, implied volatility and delta.
- Limit orders only. Selling is allowed only when covered, unless an account is approved for more.
- Limits in premium and in delta-adjusted exposure. Positions are already read and valued.

**Copied from Katalepsis.** `Application/Services/Markets/V2/Option/DeribitOptionMarketServiceV2.cs` and the Deribit
option client, and the covered-options check from `Horizen.Ria/Execution/PreTradeChecks.cs`.

**Done when.** A paper option order fills against the live Deribit book and appears in holdings with its delta.

## Phase 7 — Venues

| Venue | Added | Source in Katalepsis |
|---|---|---|
| Bitstamp | Monitoring, book, spot orders | `Infrastructure/Services/Bitstamp/` |
| Coinbase Prime | Book and spot orders (monitoring exists) | `Infrastructure/Services/CoinbasePrime/` |
| Hyperliquid | Order connection (book and monitoring exist) | None; written new |

Hyperliquid signs orders with a wallet key, not an API key. It will use an agent key that can trade and cannot
withdraw, sealed like every other trading key.

## Phase 8 — Exports and custody

- Holdings, orders and fills as CSV and Excel from the pages that show them.
- Fireblocks and Utila as read-only sources for a manager.
- Cronos, Fantom and Osmosis wallet balances.

**Copied from Katalepsis.** `Infrastructure/Services/Fireblocks/`, `Utila/`, and the three chain readers in
`Infrastructure/Services/OnChain/OnChainService.cs`. The hard-coded RPC address with a key in that file is not
copied.

---

## What stays true across all phases

- No real order has been sent to any venue yet. Each phase adds real-order code tested on recorded answers; the
  first real order, at the smallest size, is still a separate step that needs a trading key and two administrators.
- Unverified against real accounts, and marked so wherever it matters: fee signs on Bitget and Bybit, the Deribit
  history lookup, and every new call copied in phases 5 to 7.
- Each phase ends with tests, a review pass and an update to `docs/system-architecture.md`.

## Open decisions

1. Whether paper positions should ever count in NAV (phase 2 proposes: never, shown separately).
2. The amount above which an order needs a second person (phase 3 proposes: set per trading account, starting at
   the account's largest-order limit, so nothing changes until someone lowers it).
3. Whether targets are also wanted at sleeve and fund level, or only for managers and SMAs (phase 4 proposes the
   latter first).
