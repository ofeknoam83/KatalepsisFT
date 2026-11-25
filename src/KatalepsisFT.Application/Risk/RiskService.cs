using KatalepsisFT.Analytics.Holdings;
using KatalepsisFT.Analytics.Performance;
using KatalepsisFT.Analytics.Risk;
using KatalepsisFT.Application.Holdings;
using KatalepsisFT.Application.Performance;
using KatalepsisFT.Domain.Funds;
using KatalepsisFT.Domain.Instruments;
using Microsoft.EntityFrameworkCore;

namespace KatalepsisFT.Application.Risk;

/// <summary>Scenario matrix, value at risk, mandate checks, volatility, correlation and liquidity for a fund, a sleeve or a manager.</summary>
public sealed class RiskService(IFtDbContext database)
{
    public const string ReferenceAsset = "BTC";
    public const decimal DefaultParticipation = 0.10m;

    /// <summary>Days of price history that today's positions are held through for value at risk.</summary>
    private const int VarLookbackDays = 365;

    /// <summary>Days of price history each asset's beta to the reference asset is estimated on.</summary>
    private const int BetaWindowDays = 180;
    private const int VolumeWindowDays = 30;
    private const int RollingWindowDays = 30;
    private static readonly double[] Shocks = [-0.30, -0.20, -0.10, 0.10, 0.20, 0.30];
    private static readonly double[] BetaMultipliers = [0.5, 1.0, 1.5];

    public async Task<RiskDto?> GetAsync(PerformanceScope scope, int id, CancellationToken cancellationToken)
    {
        Context? context = await LoadAsync(scope, id, cancellationToken);
        if (context is null)
        {
            return null;
        }

        FundBook book = context.Book;
        List<HoldingRow> rows = context.Rows;
        IReadOnlyList<AssetExposure> assets = HoldingsAggregator.ByAsset(rows);
        Market market = await LoadMarketAsync(book.Rows.Select(r => r.Asset).Append(ReferenceAsset).Distinct().ToArray(), book.AsOf, cancellationToken);

        Dictionary<string, (double Beta, bool Assumed)> betas = assets.ToDictionary(a => a.Asset, a => Beta(market, a.Asset));
        List<AssetExposureInput> Inputs(IEnumerable<HoldingRow> source) =>
            HoldingsAggregator.ByAsset(source).Select(a => new AssetExposureInput(a.Asset, a.NetUsd, betas.TryGetValue(a.Asset, out var b) ? b.Beta : 1.0)).ToList();

        List<AssetExposureInput> inputs = Inputs(rows);
        ScenarioGridDto grid = new(
            ReferenceAsset,
            Shocks,
            BetaMultipliers,
            Shocks.Select(shock => (IReadOnlyList<decimal>)BetaMultipliers.Select(multiplier => ScenarioCalculator.Pnl(inputs, ReferenceAsset, shock, multiplier)).ToList()).ToList()
        );
        List<ScenarioRowDto> byChild = context
            .Children.Select(child =>
            {
                List<HoldingRow> childRows = rows.Where(r => child.ManagerIds.Contains(r.ManagerId)).ToList();
                List<AssetExposureInput> childInputs = Inputs(childRows);
                return new ScenarioRowDto(child.Kind, child.Id, child.Name, childRows.Sum(r => r.MarketValueUsd), Shocks.Select(shock => ScenarioCalculator.Pnl(childInputs, ReferenceAsset, shock, 1.0)).ToList());
            })
            .ToList();

        Dictionary<string, double[]> pastMoves = market.Returns(VarLookbackDays);
        VarResult var = HistoricalVar.Compute(HistoricalVar.HypotheticalPnl(assets.ToDictionary(a => a.Asset, a => a.NetUsd), pastMoves));
        // Value at risk is worked out from past price moves, so an asset without any is not in it. That is said, with
        // how much it is, rather than left for the reader to assume the figure covers the whole book.
        var uncovered = assets.Where(a => a.NetUsd != 0m && !pastMoves.ContainsKey(a.Asset)).OrderByDescending(a => Math.Abs(a.NetUsd)).ToList();
        BookTotals totals = HoldingsAggregator.Totals(rows);
        // The same year of price moves says how much of the book's swings each asset accounts for.
        Dictionary<string, double> riskShares = RiskStatistics.ExposureVarianceShares(assets.ToDictionary(a => a.Asset, a => a.NetUsd), pastMoves);

        NavHistory history = await NavHistory.LoadAsync(database, book.Managers.Select(m => m.Id).ToArray(), cancellationToken);
        double[]? Returns(int[] managerIds)
        {
            int[] valued = history.Valued(managerIds);
            return valued.Length == 0 ? null : history.Series(valued, book.AsOf).Returns();
        }

        double[] scopeReturns = Returns(context.ManagerIds) ?? [];
        List<(Child Child, double[] Returns, decimal Nav)> childSeries = context
            .Children.Select(c => (Child: c, Returns: Returns(c.ManagerIds), Nav: rows.Where(r => c.ManagerIds.Contains(r.ManagerId)).Sum(r => r.MarketValueUsd)))
            .Where(c => c.Returns is not null)
            .Select(c => (c.Child, c.Returns!, c.Nav))
            .ToList();
        // Shares of variance use today's NAV weights: a description of the current mix, not of how weights moved over time.
        double?[] shares = totals.NavUsd > 0m
            ? RiskStatistics.VarianceShares(childSeries.Select(c => (double)(c.Nav / totals.NavUsd)).ToList(), childSeries.Select(c => (IReadOnlyList<double>)c.Returns).ToList(), scopeReturns)
            : new double?[childSeries.Count];

        List<VolatilityRowDto> volatility =
        [
            new(context.Scope.Kind, context.Scope.Id, context.Scope.Name, RiskStatistics.Volatility(scopeReturns, 30), RiskStatistics.Volatility(scopeReturns, 90), null),
            .. childSeries.Select((c, i) => new VolatilityRowDto(c.Child.Kind, c.Child.Id, c.Child.Name, RiskStatistics.Volatility(c.Returns, 30), RiskStatistics.Volatility(c.Returns, 90), shares[i])),
        ];

        (List<DateOnly> rollingDates, List<SeriesDto> rolling) = Rolling(context, book.AsOf, scopeReturns, childSeries.Select(c => (c.Child.Name, c.Returns)).ToList(), market);

        // Correlation between the individual managers in scope: are they the same bet?
        List<(string Name, double[] Returns)> managerSeries = book
            .Managers.Where(m => context.ManagerIds.Contains(m.Id))
            .Select(m => (m.Name, Returns: Returns([m.Id])))
            .Where(m => m.Returns is not null)
            .Select(m => (m.Name, m.Returns!))
            .ToList();
        CorrelationDto correlation = new(
            managerSeries.Select(m => m.Name).ToList(),
            managerSeries.Select(a => (IReadOnlyList<double?>)managerSeries.Select(b => ReferenceEquals(a.Returns, b.Returns) ? 1.0 : Aligned(a.Returns, b.Returns)).ToList()).ToList()
        );

        return new RiskDto(
            context.Scope,
            book.AsOf,
            TotalsDto.From(totals),
            new VarDto(var.ValueAtRiskUsd, var.ExpectedShortfallUsd, var.Days, 0.95, uncovered.Sum(a => Math.Abs(a.NetUsd)), uncovered.Select(a => a.Asset).ToList()),
            totals.GrossUsd > 0m ? Math.Round(assets.Take(5).Sum(a => a.GrossUsd) / totals.GrossUsd, 4) : 0m,
            grid,
            byChild,
            assets.Select(a => new AssetBetaDto(a.Asset, a.NetUsd, a.GrossUsd, betas[a.Asset].Beta, betas[a.Asset].Assumed, riskShares.TryGetValue(a.Asset, out double share) ? Math.Round(share, 4) : null)).ToList(),
            Mandate(context, history, market),
            volatility,
            rollingDates,
            rolling,
            correlation
        );
    }

    public async Task<LiquidityDto?> GetLiquidityAsync(PerformanceScope scope, int id, decimal participation, CancellationToken cancellationToken)
    {
        Context? context = await LoadAsync(scope, id, cancellationToken);
        if (context is null)
        {
            return null;
        }

        FundBook book = context.Book;
        Market market = await LoadMarketAsync(context.Rows.Select(r => r.Asset).Distinct().ToArray(), book.AsOf, cancellationToken);
        (IReadOnlyList<LiquidityLine> lines, LiquidityLadder ladder) = Liquidity(context.Rows, market, participation);
        Dictionary<string, int> holders = HoldingsAggregator.ByAsset(context.Rows).ToDictionary(a => a.Asset, a => a.NetByManager.Count);

        // Each child alone is measured against the whole volume; the scope's own ladder accounts for them competing for it.
        List<LadderRowDto> children = context
            .Children.Select(c => new LadderRowDto(c.Kind, c.Id, c.Name, Dto(Liquidity(context.Rows.Where(r => c.ManagerIds.Contains(r.ManagerId)).ToList(), market, participation).Ladder)))
            .ToList();

        LiquidityLine? slowest = lines.FirstOrDefault(l => l.Days is not null);
        List<QueueLineDto> queue = slowest is null
            ? []
            : context
                .Rows.Where(r => r.Asset == slowest.Asset && r.Type != InstrumentType.Cash)
                .OrderBy(r => r.ManagerId)
                .ThenBy(r => r.Type)
                .Select(r => new QueueLineDto(r.Manager, r.Instrument, r.ExposureUsd, slowest.DailyCapacityUsd is > 0m ? (double)(Math.Abs(r.ExposureUsd) / slowest.DailyCapacityUsd.Value) : null))
                .ToList();

        return new LiquidityDto(
            context.Scope,
            book.AsOf,
            participation,
            context.Rows.Sum(r => r.MarketValueUsd),
            Dto(ladder),
            children,
            lines.Select(l => new LiquidityLineDto(l.Asset, l.GrossUsd, l.AverageDailyVolumeUsd, l.DailyCapacityUsd, l.Days, holders.GetValueOrDefault(l.Asset))).ToList(),
            queue
        );
    }

    /// <summary>The alert figures of a fund, each of its sleeves and each of its managers, from one read of the book.</summary>
    public async Task<FundMetrics?> GetFundMetricsAsync(int fundId, CancellationToken cancellationToken)
    {
        FundBook? book = await new FundBookReader(database).LoadAsync(fundId, cancellationToken);
        if (book is null || book.Rows.Count == 0)
        {
            return null;
        }

        Market market = await LoadMarketAsync(book.Rows.Select(r => r.Asset).Distinct().ToArray(), book.AsOf, cancellationToken);
        NavHistory history = await NavHistory.LoadAsync(database, book.Managers.Select(m => m.Id).ToArray(), cancellationToken);
        List<HoldingRow> all = book.Rows.ToList();
        List<ScopeMetrics?> scopes =
        [
            Measure(book, PerformanceScope.Fund, book.Fund.Id, book.Fund.Name, null, all, book.Managers.Select(m => m.Id).ToArray(), history, market),
            .. book.Sleeves.Select(s => Measure(book, PerformanceScope.Sleeve, s.Id, s.Name, s.Id, all.Where(r => r.SleeveId == s.Id).ToList(), book.Managers.Where(m => m.SleeveId == s.Id).Select(m => m.Id).ToArray(), history, market)),
            .. book.Managers.Select(m => Measure(book, PerformanceScope.Manager, m.Id, m.Name, m.SleeveId, all.Where(r => r.ManagerId == m.Id).ToList(), [m.Id], history, market)),
        ];
        return new FundMetrics(book.AsOf, scopes.OfType<ScopeMetrics>().ToList());
    }

    // ---------------------------------------------------------------- scope

    private sealed record Child(PerformanceScope Kind, int Id, string Name, int[] ManagerIds);

    private sealed record Context(ScopeDto Scope, FundBook Book, List<HoldingRow> Rows, int[] ManagerIds, List<Child> Children, List<Sleeve> MandateSleeves);

    private async Task<Context?> LoadAsync(PerformanceScope scope, int id, CancellationToken cancellationToken)
    {
        int? fundId = scope switch
        {
            PerformanceScope.Fund => await database.Funds.Where(f => f.Id == id).Select(f => (int?)f.Id).FirstOrDefaultAsync(cancellationToken),
            PerformanceScope.Sleeve => await database.Sleeves.Where(s => s.Id == id).Select(s => (int?)s.FundId).FirstOrDefaultAsync(cancellationToken),
            _ => await database.ManagerAccounts.Where(m => m.Id == id).Select(m => (int?)m.Sleeve.FundId).FirstOrDefaultAsync(cancellationToken),
        };
        if (fundId is null)
        {
            return null;
        }

        FundBook? book = await new FundBookReader(database).LoadAsync(fundId.Value, cancellationToken);
        if (book is null || book.Rows.Count == 0)
        {
            return null;
        }

        Fund fund = book.Fund;
        switch (scope)
        {
            case PerformanceScope.Fund:
                return new Context(
                    new ScopeDto(scope, fund.Id, fund.Name, fund.Id, fund.Name, fund.IsDemo, null, null),
                    book,
                    book.Rows.ToList(),
                    book.Managers.Select(m => m.Id).ToArray(),
                    book.Sleeves.Select(s => new Child(PerformanceScope.Sleeve, s.Id, s.Name, book.Managers.Where(m => m.SleeveId == s.Id).Select(m => m.Id).ToArray())).ToList(),
                    book.Sleeves.ToList()
                );
            case PerformanceScope.Sleeve:
            {
                Sleeve sleeve = book.Sleeves.Single(s => s.Id == id);
                int[] managerIds = book.Managers.Where(m => m.SleeveId == id).Select(m => m.Id).ToArray();
                return new Context(
                    new ScopeDto(scope, sleeve.Id, sleeve.Name, fund.Id, fund.Name, fund.IsDemo, sleeve.Id, sleeve.Name),
                    book,
                    book.Rows.Where(r => r.SleeveId == id).ToList(),
                    managerIds,
                    book.Managers.Where(m => m.SleeveId == id).Select(m => new Child(PerformanceScope.Manager, m.Id, m.Name, [m.Id])).ToList(),
                    [sleeve]
                );
            }

            default:
            {
                ManagerAccount manager = book.Managers.Single(m => m.Id == id);
                Sleeve sleeve = book.Sleeves.Single(s => s.Id == manager.SleeveId);
                return new Context(
                    new ScopeDto(scope, manager.Id, manager.Name, fund.Id, fund.Name, fund.IsDemo, sleeve.Id, sleeve.Name),
                    book,
                    book.Rows.Where(r => r.ManagerId == id).ToList(),
                    [manager.Id],
                    [],
                    [sleeve]
                );
            }
        }
    }

    // ---------------------------------------------------------------- market history

    /// <summary>Daily returns and recent volume per asset on a shared calendar ending at the as-of day.</summary>
    private sealed class Market(List<DateOnly> dates, Dictionary<string, double[]> returns, Dictionary<string, bool[]> traded, Dictionary<string, decimal?> volumes)
    {
        /// <summary>For each return, whether the asset had a price of its own that day.</summary>
        public bool[]? Traded(string asset) => traded.GetValueOrDefault(asset);

        public List<DateOnly> Dates => dates;

        public decimal? Volume(string asset) => volumes.GetValueOrDefault(asset);

        public double[]? Of(string asset) => returns.GetValueOrDefault(asset);

        /// <summary>The last <paramref name="days"/> returns of every asset.</summary>
        public Dictionary<string, double[]> Returns(int days) => returns.ToDictionary(r => r.Key, r => r.Value.Length > days ? r.Value[^days..] : r.Value);
    }

    private async Task<Market> LoadMarketAsync(string[] symbols, DateOnly asOf, CancellationToken cancellationToken)
    {
        DateOnly from = asOf.AddDays(-VarLookbackDays - 1);
        var bars = await database
            .PriceBars.AsNoTracking()
            .Where(b => symbols.Contains(b.Asset.Symbol) && b.Date >= from && b.Date <= asOf)
            .Select(b => new { b.Asset.Symbol, b.Date, b.Close, b.VolumeUsd })
            .ToListAsync(cancellationToken);
        var bySymbol = bars.GroupBy(b => b.Symbol).ToDictionary(g => g.Key, g => g.ToDictionary(b => b.Date, b => (b.Close, b.VolumeUsd)));

        List<DateOnly> dates = [];
        for (DateOnly day = from; day <= asOf; day = day.AddDays(1))
        {
            dates.Add(day);
        }

        Dictionary<string, double[]> returns = [];
        Dictionary<string, bool[]> traded = [];
        Dictionary<string, decimal?> volumes = [];
        foreach ((string symbol, var series) in bySymbol)
        {
            double[] r = new double[dates.Count - 1];
            bool[] had = new bool[dates.Count - 1];
            decimal? previous = series.TryGetValue(dates[0], out var first) ? first.Close : null;
            for (int t = 1; t < dates.Count; t++)
            {
                decimal? current = series.TryGetValue(dates[t], out var bar) ? bar.Close : null;
                had[t - 1] = current is > 0m;
                // A day without a price is a day without a move, and the next price is compared with the last one seen.
                if (previous is > 0m && current is > 0m)
                {
                    r[t - 1] = (double)(current.Value / previous.Value) - 1.0;
                }

                previous = current ?? previous;
            }

            returns[symbol] = r;
            traded[symbol] = had;
            List<decimal> recent = series.Where(b => b.Key > asOf.AddDays(-VolumeWindowDays)).Select(b => b.Value.VolumeUsd).ToList();
            volumes[symbol] = recent.Count > 0 ? Math.Round(recent.Average(), 2) : null;
        }

        return new Market(dates, returns, traded, volumes);
    }

    /// <summary>Fewer of the asset's own trading days than this and its beta is not estimated.</summary>
    private const int MinBetaDays = 20;

    /// <summary>Beta to the reference asset over the last half year; 1 is assumed when it cannot be estimated.</summary>
    private static (double Beta, bool Assumed) Beta(Market market, string asset)
    {
        if (string.Equals(asset, ReferenceAsset, StringComparison.OrdinalIgnoreCase))
        {
            return (1.0, false);
        }

        double[]? own = market.Of(asset), reference = market.Of(ReferenceAsset);
        if (own is null || reference is null)
        {
            return (1.0, true);
        }

        // Only days on which the asset itself traded are compared, each with the reference's move since the asset's
        // previous price. Taking every calendar day would set a share's weekend, or the months before a coin was
        // listed, as "no move" against a moving reference, and pull the beta towards zero.
        int n = Math.Min(BetaWindowDays, Math.Min(own.Length, reference.Length));
        bool[] had = market.Traded(asset)!;
        List<double> ownMoves = [],
            referenceMoves = [];
        double since = 1.0;
        bool open = false;
        for (int i = own.Length - n, j = reference.Length - n; i < own.Length; i++, j++)
        {
            since *= 1.0 + reference[j];
            if (!had[i])
            {
                continue;
            }

            if (open)
            {
                ownMoves.Add(own[i]);
                referenceMoves.Add(since - 1.0);
            }

            open = true;
            since = 1.0;
        }

        double? beta = ownMoves.Count < MinBetaDays ? null : RiskStatistics.Beta(ownMoves, referenceMoves);
        return beta is null ? (1.0, true) : (Math.Round(beta.Value, 4), false);
    }

    // ---------------------------------------------------------------- volatility

    private static (List<DateOnly> Dates, List<SeriesDto> Series) Rolling(Context context, DateOnly asOf, double[] scopeReturns, List<(string Name, double[] Returns)> children, Market market)
    {
        double[] scopeRolling = RiskStatistics.RollingVolatility(scopeReturns, RollingWindowDays);
        if (scopeRolling.Length == 0)
        {
            return ([], []);
        }

        // Every series is laid on the scope's calendar, which ends at the as-of day; shorter ones start later.
        int length = scopeRolling.Length;
        List<DateOnly> dates = Enumerable.Range(0, length).Select(i => asOf.AddDays(i - (length - 1))).ToList();
        List<double?> Pad(double[] values) => [.. Enumerable.Repeat<double?>(null, Math.Max(0, length - values.Length)), .. values.Skip(Math.Max(0, values.Length - length)).Select(v => (double?)v)];

        List<SeriesDto> series = [new(context.Scope.Name, Pad(scopeRolling))];
        series.AddRange(children.Select(c => new SeriesDto(c.Name, Pad(RiskStatistics.RollingVolatility(c.Returns, RollingWindowDays)))));
        if (market.Of(ReferenceAsset) is double[] reference)
        {
            series.Add(new SeriesDto(ReferenceAsset, Pad(RiskStatistics.RollingVolatility(reference, RollingWindowDays))));
        }

        return (dates, series);
    }

    /// <summary>Correlation over the days both series cover, aligned on the most recent day.</summary>
    private static double? Aligned(double[] a, double[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        return RiskStatistics.Correlation(a[^n..], b[^n..]);
    }

    // ---------------------------------------------------------------- mandate

    private static List<MandateCheckDto> Mandate(Context context, NavHistory history, Market market)
    {
        List<MandateCheckDto> result = [];
        foreach (Sleeve sleeve in context.MandateSleeves.Where(s => s.Limits.Count > 0))
        {
            int[] managerIds = context.Book.Managers.Where(m => m.SleeveId == sleeve.Id).Select(m => m.Id).ToArray();
            ScopeMetrics? m = Measure(context.Book, PerformanceScope.Sleeve, sleeve.Id, sleeve.Name, sleeve.Id, context.Book.Rows.Where(r => r.SleeveId == sleeve.Id).ToList(), managerIds, history, market);
            if (m is null)
            {
                continue;
            }

            MandateState state = new(m.GrossOverNav, m.NetOverNav, m.LargestAsset, m.LargestAssetOverNav, m.WorstManager, m.WorstDrawdown, m.SlowestAsset, m.SlowestDays);
            result.AddRange(MandateChecker.Check(sleeve.Limits.Select(l => (l.Kind, l.Value)), state).Select(c => new MandateCheckDto(sleeve.Id, sleeve.Name, c.Kind, c.Rule, c.Current, c.Limit, c.WithinLimit)));
        }

        return result;
    }

    /// <summary>The figures alert rules and mandate limits are compared with; null when the scope has no NAV.</summary>
    private static ScopeMetrics? Measure(FundBook book, PerformanceScope kind, int id, string name, int? sleeveId, List<HoldingRow> rows, int[] managerIds, NavHistory history, Market market)
    {
        BookTotals totals = HoldingsAggregator.Totals(rows);
        if (totals.NavUsd <= 0m)
        {
            return null;
        }

        IReadOnlyList<AssetExposure> assets = HoldingsAggregator.ByAsset(rows);
        AssetExposure? largest = assets.FirstOrDefault();
        (string Name, double Drawdown)? worst = book
            // A retired manager's drawdown is history, not a risk the fund still runs.
            .Managers.Where(m => m.IsActive && managerIds.Contains(m.Id) && history.Valued([m.Id]).Length == 1)
            .Select(m => (m.Name, Drawdown: CurrentDrawdown(history.Series([m.Id], book.AsOf).Returns())))
            .OrderBy(m => m.Drawdown)
            .Select(m => ((string Name, double Drawdown)?)m)
            .FirstOrDefault();
        LiquidityLine? slowest = Liquidity(rows, market, DefaultParticipation).Lines.FirstOrDefault(l => l.Days is not null);
        VarResult var = HistoricalVar.Compute(HistoricalVar.HypotheticalPnl(assets.ToDictionary(a => a.Asset, a => a.NetUsd), market.Returns(VarLookbackDays)));
        int[] valued = history.Valued(managerIds);
        double[] returns = valued.Length == 0 ? [] : history.Series(valued, book.AsOf).Returns();

        return new ScopeMetrics(
            kind,
            id,
            name,
            sleeveId,
            totals.NavUsd,
            Math.Round(totals.GrossUsd / totals.NavUsd, 6),
            Math.Round(totals.NetUsd / totals.NavUsd, 6),
            largest?.Asset,
            largest is null ? 0m : Math.Round(largest.GrossUsd / totals.NavUsd, 6),
            worst?.Name,
            worst?.Drawdown,
            slowest?.Asset,
            slowest?.Days,
            var.Days > 0 ? Math.Round(var.ValueAtRiskUsd / totals.NavUsd, 6) : null,
            returns.Length > 0 ? returns[^1] : null
        );
    }

    /// <summary>How far below its own high-water mark a return series stands today; 0 at a new high.</summary>
    private static double CurrentDrawdown(double[] returns)
    {
        IReadOnlyList<decimal> drawdown = BacktestMetricsCalculator.DrawdownSeries(ReturnCalculator.GrowthIndex(returns));
        return (double)drawdown[^1];
    }

    // ---------------------------------------------------------------- liquidity

    private static (IReadOnlyList<LiquidityLine> Lines, LiquidityLadder Ladder) Liquidity(List<HoldingRow> rows, Market market, decimal participation)
    {
        // Gross per asset position by position: a manager long spot and short the perp needs volume to close both legs.
        IReadOnlyList<LiquidityLine> lines = LiquidityCalculator.Lines(HoldingsAggregator.ByAsset(rows).Select(a => new LiquidityInput(a.Asset, a.GrossUsd, market.Volume(a.Asset))), participation);
        return (lines, LiquidityCalculator.Ladder(lines, rows.Where(r => r.Type == InstrumentType.Cash).Sum(r => r.MarketValueUsd)));
    }

    private static LadderDto Dto(LiquidityLadder ladder) =>
        new(ladder.WithinOneDayUsd, ladder.OneToFiveDaysUsd, ladder.FiveToTwentyDaysUsd, ladder.BeyondTwentyDaysUsd, ladder.UnknownUsd, ladder.TotalUsd);
}
