# Katalepsis FT — system architecture (DRAFT, decisions recorded, awaiting approval)

Companion to `plan.md`. That file says what the product does; this one says how the system is put together. Planning only; nothing here is built.

The seven architecture decisions are answered and recorded in section 11. Items marked **(proposed)** are my recommendation and have not been put to the owner as a question. Section 11 also lists three consequences of the answers that need a yes or no.

## 1. Facts this design rests on

Read in this session.

**Katalepsis code**

- The existing live-position worker is one `BackgroundService` that re-reads the wallet list from the database every 30 seconds, starts one long-running task per wallet, and cancels tasks for wallets that disappear (`PerpetualPositionsWatcher/PerpetualPositionsWatcherWorker.cs:84-210`).
- It starts wallets 10 seconds apart because starting them together hits venue rate limits (`PerpetualPositionsWatcherWorker.cs:187-188`).
- Venue dispatch is a `switch` on the market code with one near-identical method per venue (`PerpetualPositionsWatcherWorker.cs:129-185`, `:250-374`). Adding a venue means editing the worker.
- Live positions are written to Redis with a 5-minute expiry and three retries (`Application/Services/Positions/Perpetual/PerpetualPositionsWatcher.cs:100-127`). Stream trouble is only logged (`:55-98`). Alert de-duplication is an in-memory dictionary, lost on restart (`:34`).
- The existing venue clients are trading-oriented: balances are asked one asset at a time (`Horizen.Ria/Integration/Venues/ExchangeVenueGateway.cs:66`, `:113`).
- The existing clients answer zero when a balance or position request fails, so a failed read looks like an empty account (`ExchangeVenueGateway.cs:60-61`, `:104-105`). Some venues report perp sizes without a sign (`ExchangeVenueGateway.cs:136`).
- Venue libraries in use: Binance.Net 12.11.1, Bybit.Net 6.10.0, Kucoin.Net 8.10.1, JK.OKX.Net 4.10.2, JK.Mexc.Net 4.9.0; Deribit is a hand-written client over WebSocket4Net (`Infrastructure/Infrastructure.csproj:10-29`).

**Render (from render.com/docs, read 22 Nov 2026)**

- A blueprint can define web services, private services, background workers, cron jobs, Key Value and Postgres, all building from one repository with different Dockerfile paths and commands.
- Background workers are always on, accept no inbound traffic, can open outbound WebSockets, and have no free tier.
- Key Value runs Valkey 8. The docs do not mention pub/sub or Redis modules either way.
- Services in one workspace and region share a private network. Key Value's internal address is unauthenticated unless internal authentication is switched on.
- Web services accept WebSockets with no maximum duration. Connections are closed when an instance is replaced on deploy. There are no sticky sessions; with more than one web instance, connections land on a random instance.
- A pre-deploy command runs after the build and before the new version starts, on paid web services and workers.
- Postgres point-in-time recovery covers 3 days on the smaller paid plans and 7 days on Pro. TimescaleDB is listed but without its community features.
- Secrets declared with `sync: false` are asked for once, when the blueprint is first created.
- Preview environments per pull request need a Pro workspace.

## 2. Processes and resources

```
                         browsers
                            │  HTTPS + WebSocket (SignalR)
                            ▼
                   ┌─────────────────┐
                   │  web service ×2 │  API, sign-in, serves the Angular app,
                   │  (ASP.NET Core) │  pushes live updates
                   └───┬─────────┬───┘
            SQL        │         │  read latest books, subscribe to changes
                       ▼         ▼
              ┌────────────┐  ┌────────────┐
              │  Postgres  │  │ Key Value  │
              └────────────┘  └────────────┘
                 ▲   ▲   ▲      ▲   ▲   ▲   write books and prices, publish changes
        ┌────────┘   │   └──────┼───┼───┼────────┐
   ┌────┴─────────┐ ┌┴──────────┴─┐ ┌┴──────────┴───┐
   │ CeFi worker  │ │ DeFi worker │ │  core worker  │
   │ 10 exchanges │ │ 5 venues,   │ │ price feeds,  │
   │ streams      │ │ on-chain    │ │ aggregation,  │
   │              │ │ polling     │ │ alerts,       │
   │              │ │             │ │ 5-min samples │
   └──────┬───────┘ └──────┬──────┘ └──────┬────────┘
          ▼                ▼               ▼          outbound only
     CeFi venues     chains / DeFi     price feeds (crypto, equities, futures)

              ┌─────────────────┐
              │    cron job     │  end-of-day snapshot, price history backfill
              └─────────────────┘  (same image, different command)
```

| Resource | Render type | Job |
|---|---|---|
| Web (2 instances) | web service, Docker | REST API, built-in sign-in, SignalR hub, static Angular files, health endpoint |
| CeFi worker | background worker, Docker | Streams and snapshots for the 10 exchanges; builds and values those books |
| DeFi worker | background worker, Docker | Polls the 5 DeFi venues and wallet addresses; builds and values those books |
| Core worker | background worker, Docker | Price feeds; fund-wide aggregation; alert evaluation; 5-minute samples |
| Snapshot | cron job, Docker | End-of-day snapshot at 00:00 UTC; nightly price-history backfill and integrity check |
| Database | Postgres | System of record |
| Cache | Key Value (Valkey 8) | Latest books and prices; change notifications between workers and to web |

That is eight paid resources in one production environment: two web instances, three workers, a cron job, Postgres and Key Value.

One Docker image holds every executable; each Render service starts a different command (the worker takes its group as an argument). Database migrations run as the web service's pre-deploy command.

**Why a core worker.** Alerts such as "VFY held by five managers" look across books that live in different workers, so they need one place that sees everything. The core worker reads all books from Key Value, owns prices, and is the only evaluator of alerts and the only writer of intraday samples. This follows from splitting the workers and is listed for confirmation in section 11.

## 3. Solution layout

Same layering as Katalepsis: Domain → Persistence + Infrastructure → Application → entry points.

| Project | Contents |
|---|---|
| `KatalepsisFT.Domain` | Entities, enums, value objects (fund, sleeve, manager, instrument, position, alert rule) |
| `KatalepsisFT.Analytics` | Pure calculators with no I/O: returns, metrics, exposure, risk, liquidity, scenario. The copied Katalepsis calculators land here |
| `KatalepsisFT.Application` | MediatR queries and commands, validators, service interfaces |
| `KatalepsisFT.Persistence` | EF Core context, Npgsql, entity configurations, migrations |
| `KatalepsisFT.Infrastructure` | Key Value access, key encryption, notifications, price feeds |
| `KatalepsisFT.Venues` | The adapter contract and one folder per venue |
| `KatalepsisFT.Web` | Controllers, SignalR hub, sign-in, Angular app under `ClientApp/` |
| `KatalepsisFT.Worker` | Connection supervisor, book builder, alert evaluator |
| `KatalepsisFT.Jobs` | Snapshot and backfill commands for the cron job |
| `*.Tests` | One test project per project above; adapter contract tests shared by all venues |

Analytics is its own project so every number on screen comes from code that can be unit-tested without a database or a venue.

## 4. Venue adapters

One contract for all 15 venues, read-only by construction: the interface has no method that places, changes or cancels anything.

```
IVenueAdapter
  Capabilities            spot / perp / future / option / pool; has stream; can check key permissions
  CheckCredentials()      → permissions the key holds (read, trade, withdraw)
  GetSnapshot()           → every balance and position of the account, in one normalised shape
  Stream(onChange)        → pushes changes; ends with a reason when the connection is lost
  GetMarks(instruments)   → mark prices where the venue provides them
```

Rules that follow from what Katalepsis taught us (section 1):

- **A failed read is an error, never an empty book.** Adapters return either a complete snapshot or a failure. The live book is only replaced by a complete snapshot.
- **Positions are always signed** when they leave the adapter. A venue that reports unsigned sizes is resolved inside its adapter (from the venue's side field), not downstream.
- **One bulk read per account**, not one request per asset.
- **Normalised output.** Every adapter emits the same position shape: instrument (type, underlying asset, expiry, strike, multiplier), signed quantity, entry, venue mark, margin, liquidation price, venue timestamp.
- **Symbol mapping lives in data**, a table of venue symbol → instrument, so a new listing is a row and not a release.
- Adapters are registered by venue name. The worker never switches on venue; adding a venue adds a folder and a registration line.
- Venues without a stream (and on-chain reads) are polled on an interval set per venue.

The copied Katalepsis clients sit behind this contract. Only their read methods are carried over.

Every adapter passes the same contract test suite against recorded venue responses: complete snapshot, failed request, dropped stream, unsigned size, unknown symbol, rate-limit response.

## 5. Workers: from venue to screen

Steps 1–3, 5, 6 and 9 run in the CeFi and DeFi workers for their own connections. Steps 4, 7 and 8 run in the core worker. A manager with accounts on both kinds of venue has one book per connection; the manager's total is the sum, computed in the core worker and in web queries.

1. **Supervisor.** Reads the list of connections from Postgres on a short interval. Starts one supervised task per connection, staggered per venue to respect rate limits; stops tasks for removed connections. A task that fails restarts with increasing delay, capped, with jitter.
2. **Start-up of each connection.** Full snapshot first, then the stream. After any reconnect, a full snapshot again before stream updates are trusted.
3. **Live book.** Per manager account, the worker keeps the current positions in memory and mirrors them to Key Value. Each book carries its "as of" time and a state: live, stale, failed.
4. **Prices.** The core worker holds one shared price stream per venue (not per account) and writes a price table to Key Value; the CeFi and DeFi workers subscribe to it for valuation. Traditional instruments come from the free feed on a polling interval, with their delay recorded.
5. **Valuation.** On a position or price change, the affected book is revalued by the pure valuer, throttled so one book is revalued at most about once a second.
6. **Publish.** The worker publishes "book changed" on a Key Value channel. The web service subscribes and pushes to browsers that are viewing that fund, sleeve, manager or asset.
7. **Alerts.** Rules are evaluated on the revalued state, throttled per rule. Alert state (open, acknowledged, resolved) lives in Postgres, so a restart cannot re-raise or forget an alert.
8. **Persistence.** Every 5 minutes the core worker writes a sample of each book and of NAV to Postgres; samples are kept 90 days and removed by the nightly job. The end-of-day snapshot is written by the cron job from the latest book.
9. **Staleness.** A book with no update within its venue's expected interval is marked stale; the screens show the age, and totals keep the last known values rather than dropping them.

What lives where:

| Data | Home | Lost on restart? |
|---|---|---|
| Structure, instruments, connections, encrypted keys, rules, alerts, users | Postgres | No |
| End-of-day position and NAV snapshots, cash flows, price history | Postgres | No |
| Intraday samples | Postgres | No |
| Latest book and latest prices | Worker memory, mirrored in Key Value | Rebuilt from the venues in seconds to minutes |
| Open browser connections | Web memory | Browsers reconnect by themselves |

Key Value holds nothing that cannot be rebuilt. If it is emptied, the worker refills it.

## 6. Web: reads, live updates, analytics

- **Reads.** Pages load through REST endpoints (MediatR queries). History and analytics come from Postgres; "now" comes from Key Value.
- **Live updates.** One SignalR hub. A browser joins groups for what it is looking at (fund, sleeve, manager, asset). Each of the two web instances subscribes to worker notifications on Key Value by itself and pushes to its own connected browsers, so no message has to be relayed between instances. If the socket drops, the page falls back to refreshing on an interval and says so.
- **Two instances, no sticky sessions.** Nothing a request needs may live in one instance's memory: the sign-in cookie's key ring is stored in Postgres so either instance can read a session, SignalR uses WebSockets only (the fallback transports need stickiness), and the in-memory analytics cache is only an optimisation.
- **Analytics.** Aggregations (exposure, overlap, liquidity ladder, scenario matrix) are computed on request from the current book by the pure calculators, and cached briefly in memory keyed by scope and book version. Performance and risk statistics that need history (returns, volatility, correlation, VaR) are computed from end-of-day snapshots and recomputed when a new snapshot lands.
- **Frontend.** Angular 21, standalone components, OnPush, lazy routes per area, ng-zorro, Chart.js, NGXS for the global selectors (period, benchmark, instrument type) and the live book, as in Katalepsis.

## 7. Data model in Postgres (groups)

- **Structure:** fund, sleeve, manager account, mandate limit, benchmark, benchmark constituent.
- **Reference:** asset, instrument, venue, venue symbol mapping.
- **Connections:** venue connection (manager, venue, label, encrypted credentials or wallet address, state, last success).
- **Positions:** end-of-day position snapshot, intraday sample, manual position, cash flow.
- **Valuation history:** NAV snapshot per manager per day; price bar per asset per day.
- **Alerts:** rule, alert, alert action (the audit trail).
- **Access:** user, role (ASP.NET Identity tables), sign-in audit.

Prices are stored as `decimal(18,8)`, the Katalepsis convention (decision A9); quantities as `decimal(28,8)` (decision A9a); USD values as `decimal(18,2)`.

Known limits of 8 decimals, accepted with that decision:

- A price below about $0.0001 keeps only a few significant digits (a $0.00000123 token is stored to three digits), so the value of a large holding in such a token can be off by up to roughly half a percent.
- On-chain balances reported with 18 decimals are rounded to 8; totals can differ from a wallet explorer in the last digits.
- `decimal(18,8)` holds at most ten digits before the point, which a position in a very low-priced token can exceed. That is why quantities alone use `decimal(28,8)`.

Snapshot tables grow by (managers × positions × days). At this scale plain tables with an index on (manager, date) are enough; no partitioning or TimescaleDB in version 1.

## 8. Security

- **Read-only by construction.** No code path can place an order: the adapter contract has no such method, and the trading parts of the copied clients are not brought over.
- **Key permissions are checked before a key is saved**, and re-checked periodically. A key that can trade or withdraw is rejected where the venue lets us see permissions; where it does not, the connection is flagged as "permissions unverified".
- **Keys at rest** are encrypted with a master key held as a Render environment secret, not in the database. They are decrypted only inside the CeFi worker (and briefly in the web service when a new key is checked), never sent to the browser, never logged. Anyone with admin access to the Render workspace can read the master key; that is the accepted limit of this choice.
- **Sign-in:** ASP.NET Identity, cookie session, Admin and Viewer roles, optional two-step sign-in, lockout after repeated failures. Only Admin endpoints can touch connections, structure, rules and users.
- **Network:** only the web service is reachable from the internet. Worker, Postgres and Key Value are private. Key Value internal authentication is switched on.
- **Secrets** (database URL, master key, notification webhooks, price-feed keys) are Render environment secrets, not in the repository.
- **Audit:** who changed structure, connections, rules and alert states, and when.

## 9. Delivery and operations

- **Repository:** `ofeknoam83/KatalepsisFT`, one solution, `render.yaml` at the root, one multi-stage Dockerfile.
- **CI (GitHub Actions):** restore, build with warnings as errors, unit and contract tests, integration tests against a Postgres service container, Angular lint and build. Required to pass before merge.
- **Deploy:** every merge to `main` that passes CI deploys to production automatically. Migrations run in the pre-deploy step; the two web instances are replaced one at a time once the health check passes.
- **There is no staging**, so the safeguards are in the pipeline and the code:
  - CI must pass before Render deploys a commit. Direct pushes to `main` are allowed; a pull request is not required.
  - Migrations are backward compatible (add first, remove in a later release), because during a deploy the old and new web instances run against the same database at the same time.
  - Venue adapters are tested against recorded venue responses in CI. A change to an adapter is first seen against the real venue in production; this is the main risk of having no staging.
  - The demo fund lives in production as its own labelled fund, excluded from cross-fund totals, so screens can be checked on it without touching real books.
  - A failed deploy is rolled back from Render to the previous build.
- **Deploying a worker** drops that worker's venue connections; it rebuilds its books on start and screens show "reconnecting" for that period. Build filters limit each service to its own code: a front-end change restarts no worker, and a DeFi adapter change does not restart the CeFi streams.
- **Health:** web health endpoint checks database and Key Value. The worker writes a heartbeat and per-connection state that the web service exposes on the connections page and that drives the stale-data alert.
- **Logs and metrics:** structured logs (Serilog) to Render's log stream; counters per venue for reconnects, rate-limit responses and snapshot duration.
- **Backups:** Render Postgres point-in-time recovery; a restore is rehearsed once before go-live.

## 10. Failure modes the design must survive

| Event | Behaviour |
|---|---|
| One venue is down or rate-limits us | That venue's books go stale with their age shown; other venues unaffected; back-off, no tight loop |
| A stream drops | Reconnect, full snapshot, then resume; book marked stale meanwhile |
| A venue request fails | Treated as failure; the last complete book is kept. Never an empty book |
| A CeFi or DeFi worker restarts or is deployed | Its books are rebuilt from the venues; the other workers' books are untouched; alerts unaffected because their state is in Postgres |
| The core worker restarts | Prices pause and books are valued at the last price until it is back; no alerts are evaluated and no samples written meanwhile; the gap is recorded |
| One web instance fails | The other keeps serving; browsers on the failed one reconnect to it |
| Key Value restarts or is emptied | Worker refills it; web shows stale until then |
| Web restarts or is deployed | Browsers reconnect and reload the current state |
| Postgres unavailable | Live view continues from Key Value; history and writes fail visibly; samples are not silently dropped (the worker buffers a bounded amount and reports the gap) |
| A key is revoked or expires | Connection goes to failed with the venue's reason; alert raised |
| Cron job misses the end-of-day run | The next run backfills the missing day from the intraday samples, and the day is marked as backfilled |
| Two worker instances run by mistake | A lease in Postgres per connection ensures only one instance owns it |

## 11. Decisions

Answered by the owner:

| # | Decision | Answer |
|---|---|---|
| A1 | Worker layout | Workers split by venue group |
| A2 | Intraday history | A sample every 5 minutes, kept 90 days |
| A3 | Web instances | Two from the start |
| A4 | Environments | Production only |
| A5 | Master key for stored API keys | Render environment secret |
| A6 | Deploy trigger | Automatic on merge to `main` |
| A7 | Venue client code | Same library family as Katalepsis; hand-written only where no maintained package exists |

Consequences of those answers, also answered by the owner:

| # | Consequence | Answer |
|---|---|---|
| C1 | Where fund-wide aggregation, alerts, prices and samples run | A third "core" worker (section 2) |
| C2 | Protection of `main` | Tests must pass before a deploy; direct pushes to `main` are allowed, no pull request required |
| C3 | Where the demo fund lives | In production, as its own labelled fund, excluded from cross-fund totals |

One technical constraint stays regardless of C2: migrations must work with both the old and the new version, because the two web instances are replaced one at a time and briefly run different versions against the same database.

Formerly proposed items, now answered:

| # | Decision | Answer |
|---|---|---|
| A8 | Project layout | Nine projects by responsibility (section 3) |
| A9 | Number precision | 8 decimals for quantities and prices, as in Katalepsis |
| A10 | Frontend state | NGXS, as in Katalepsis |

| A9a | Large quantities | Quantity columns are `decimal(28,8)`: 8 decimals, more whole digits. Prices stay `decimal(18,8)` |

## 12. Verified since this was written

- Pub/sub works on Render Key Value (checked 22 Nov 2026 on the deployed services: the web service's `/health/ready` showed heartbeats from all three workers, which arrive only over a pub/sub channel).
- The plan names in `render.yaml` (`starter`, `basic-256mb`) and `autoDeployTrigger: checksPass` were accepted by Render.

## 13. Not yet verified

- Whether maintained .NET client packages exist for Bitget, Gate, Kraken, Hyperliquid and dYdX in the library family Katalepsis uses.
- Which venues let a key's permissions be read.
- Render instance sizes needed for the worker with roughly 15–40 open streams; measured in Phase 6.
- Katalepsis source files listed in the plan as "from the inventory" remain to be read when each is copied.
