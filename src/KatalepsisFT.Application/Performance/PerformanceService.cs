using KatalepsisFT.Analytics.Performance;
using KatalepsisFT.Domain.Benchmarks;
using KatalepsisFT.Domain.Funds;
using KatalepsisFT.Domain.Instruments;
using Microsoft.EntityFrameworkCore;

namespace KatalepsisFT.Application.Performance;

/// <summary>Returns, statistics, benchmark comparison and profit attribution for a fund, a sleeve or a manager.</summary>
public sealed class PerformanceService(IFtDbContext database)
{
    /// <summary>The most points a chart series carries; longer histories are thinned, keeping both ends and the trough.</summary>
    private const int MaxChartPoints = BacktestConstants.MaxChartPoints;

    public async Task<PerformanceDto?> GetAsync(PerformanceScope scope, int id, int? benchmarkId, PerformancePeriod period, CancellationToken cancellationToken)
    {
        Resolved? resolved = await ResolveAsync(scope, id, cancellationToken);
        if (resolved is null)
        {
            return null;
        }

        List<Benchmark> benchmarks = await database.Benchmarks.AsNoTracking().Include(b => b.Constituents).OrderBy(b => b.Name).ToListAsync(cancellationToken);
        Benchmark? benchmark = benchmarks.FirstOrDefault(b => b.Id == (benchmarkId ?? resolved.DefaultBenchmarkId)) ?? benchmarks.FirstOrDefault();
        if (benchmark is null)
        {
            return null;
        }

        NavHistory history = await NavHistory.LoadAsync(database, resolved.FundManagers.Select(m => m.Id).ToArray(), cancellationToken);
        int[] scopeManagerIds = history.Valued(resolved.ScopeManagers.Select(m => m.Id));
        if (scopeManagerIds.Length == 0)
        {
            return null;
        }

        DateOnly asOf = history.AsOf(scopeManagerIds);
        NavSeries series = history.Series(scopeManagerIds, asOf);
        double[] returns = series.Returns();
        double[] benchmarkReturns = await BenchmarkReturnsAsync(benchmark, series.Dates, cancellationToken);

        DateOnly from = PeriodStart(period, series.Dates[0], asOf);
        int start = Math.Max(0, series.Dates.FindIndex(d => d >= from));
        from = series.Dates[start];
        double[] slice = returns[start..];
        double[] benchmarkSlice = benchmarkReturns[start..];
        List<DateOnly> dates = series.Dates.GetRange(start, series.Dates.Count - start);

        decimal[] equity = ReturnCalculator.GrowthIndex(slice);
        decimal[] benchmarkEquity = ReturnCalculator.GrowthIndex(benchmarkSlice);
        IReadOnlyList<decimal> drawdown = BacktestMetricsCalculator.DrawdownSeries(equity);
        int trough = Enumerable.Range(0, drawdown.Count).MinBy(i => drawdown[i]);
        IReadOnlyList<int> keep = SeriesDownsampler.Indices(dates.Count, MaxChartPoints, trough);

        List<ChildPerformanceDto> children = [];
        foreach ((PerformanceScope kind, int childId, string name, int[] managerIds) in resolved.Children)
        {
            int[] valued = history.Valued(managerIds);
            if (valued.Length == 0)
            {
                continue;
            }

            NavSeries child = history.Series(valued, asOf);
            if (child.Dates.Count == 0)
            {
                continue;
            }

            double[] childReturns = child.Returns();
            // A child that started later than the scope is compared over its own days only.
            int childStart = Math.Max(0, child.Dates.FindIndex(d => d >= from));
            double[] childSlice = childReturns[childStart..];
            int offset = series.Dates.IndexOf(child.Dates[childStart]);
            double[] childBenchmark = benchmarkReturns[offset..];
            (double contribution, decimal pnl, double weight) = Contribution(series, start, child);
            children.Add(new ChildPerformanceDto(kind, childId, name, child.Navs[^1], Stats(childSlice), Compare(childSlice, childBenchmark), contribution, pnl, weight));
        }

        AttributionDto attribution = await AttributeAsync(resolved, scopeManagerIds, from, asOf, series, start, cancellationToken);

        return new PerformanceDto(
            resolved.Scope,
            Dto(benchmark),
            benchmarks.Select(Dto).ToList(),
            period,
            from,
            asOf,
            keep.Select(i => dates[i]).ToList(),
            keep.Select(i => (double)equity[i] - 1.0).ToList(),
            keep.Select(i => (double)benchmarkEquity[i] - 1.0).ToList(),
            keep.Select(i => (double)drawdown[i]).ToList(),
            Stats(slice),
            Stats(benchmarkSlice),
            Compare(slice, benchmarkSlice),
            Periods(series.Dates, returns, benchmarkReturns, asOf),
            Monthly(dates, slice, benchmarkSlice),
            children,
            attribution
        );
    }

    public async Task<AssetPerformanceDto?> GetAssetAsync(string symbol, int? benchmarkId, PerformancePeriod period, CancellationToken cancellationToken)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        Asset? asset = await database.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Symbol == symbol, cancellationToken);
        List<Benchmark> benchmarks = await database.Benchmarks.AsNoTracking().Include(b => b.Constituents).OrderBy(b => b.Name).ToListAsync(cancellationToken);
        Benchmark? benchmark = benchmarks.FirstOrDefault(b => b.Id == benchmarkId) ?? benchmarks.FirstOrDefault(b => b.Kind == BenchmarkKind.SingleAsset) ?? benchmarks.FirstOrDefault();
        if (asset is null || benchmark is null)
        {
            return null;
        }

        var bars = await database.PriceBars.AsNoTracking().Where(b => b.AssetId == asset.Id).OrderBy(b => b.Date).Select(b => new { b.Date, b.Close }).ToListAsync(cancellationToken);
        if (bars.Count < 2)
        {
            return null;
        }

        DateOnly asOf = bars[^1].Date;
        DateOnly from = PeriodStart(period, bars[0].Date, asOf);
        var window = bars.Where(b => b.Date >= from).ToList();
        if (window.Count < 2)
        {
            return null;
        }

        // One point per calendar day, a day without trading keeping the last price. Every annual figure here counts 365
        // days to the year; a series with only trading days in it (shares) would have its volatility and annual
        // return overstated, and would be compared with the benchmark over the wrong days.
        List<DateOnly> dates = [];
        List<decimal> closes = [];
        int at = 0;
        for (DateOnly day = window[0].Date; day <= asOf; day = day.AddDays(1))
        {
            while (at + 1 < window.Count && window[at + 1].Date <= day)
            {
                at++;
            }

            dates.Add(day);
            closes.Add(window[at].Close);
        }

        double[] returns = ReturnCalculator.DailyReturns(closes);
        double[] benchmarkReturns = await BenchmarkReturnsAsync(benchmark, dates, cancellationToken);
        decimal[] equity = ReturnCalculator.GrowthIndex(returns);
        decimal[] benchmarkEquity = ReturnCalculator.GrowthIndex(benchmarkReturns);
        IReadOnlyList<int> keep = SeriesDownsampler.Indices(dates.Count, MaxChartPoints);
        return new AssetPerformanceDto(
            symbol,
            Dto(benchmark),
            benchmarks.Select(Dto).ToList(),
            period,
            dates[0],
            asOf,
            keep.Select(i => dates[i]).ToList(),
            keep.Select(i => (double)equity[i] - 1.0).ToList(),
            keep.Select(i => (double)benchmarkEquity[i] - 1.0).ToList(),
            Stats(returns),
            Stats(benchmarkReturns),
            Compare(returns, benchmarkReturns)
        );
    }

    /// <summary>
    /// What one child added to the scope's return from <paramref name="start"/> on. Each day the child's profit is
    /// set against the money the whole scope had at work that day, and that day's share is scaled by how far the scope
    /// had already grown, so the children's figures add up to the scope's compounded return exactly.
    /// </summary>
    public static (double Contribution, decimal PnlUsd, double AverageWeight) Contribution(NavSeries scope, int start, NavSeries child)
    {
        Dictionary<DateOnly, (decimal Pnl, decimal Previous)> byDay = [];
        for (int k = 1; k < child.Dates.Count; k++)
        {
            byDay[child.Dates[k]] = (child.Navs[k] - child.Navs[k - 1] - child.Flows[k], child.Navs[k - 1]);
        }

        double contribution = 0,
            growth = 1,
            weights = 0;
        decimal pnl = 0m;
        int days = 0;
        for (int t = start + 1; t < scope.Dates.Count; t++)
        {
            decimal invested = scope.Navs[t - 1] + scope.Flows[t];
            double scopeReturn = invested > 0m ? (double)(scope.Navs[t] / invested) - 1.0 : 0.0;
            if (byDay.TryGetValue(scope.Dates[t], out (decimal Pnl, decimal Previous) day))
            {
                pnl += day.Pnl;
                if (invested > 0m)
                {
                    contribution += (double)(day.Pnl / invested) * growth;
                }

                if (scope.Navs[t - 1] > 0m)
                {
                    weights += (double)(day.Previous / scope.Navs[t - 1]);
                }
            }

            days++;
            growth *= 1.0 + scopeReturn;
        }

        return (contribution, Math.Round(pnl, 2), days == 0 ? 0.0 : weights / days);
    }

    // ---------------------------------------------------------------- scope

    private sealed record Resolved(
        ScopeDto Scope,
        int? DefaultBenchmarkId,
        List<ManagerAccount> FundManagers,
        List<ManagerAccount> ScopeManagers,
        List<(PerformanceScope Kind, int Id, string Name, int[] ManagerIds)> Children,
        Dictionary<int, string> SleeveNames
    );

    private async Task<Resolved?> ResolveAsync(PerformanceScope scope, int id, CancellationToken cancellationToken)
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

        Fund fund = await database.Funds.AsNoTracking().FirstAsync(f => f.Id == fundId, cancellationToken);
        List<Sleeve> sleeves = await database.Sleeves.AsNoTracking().Where(s => s.FundId == fundId).OrderBy(s => s.SortOrder).ThenBy(s => s.Id).ToListAsync(cancellationToken);
        List<ManagerAccount> managers = await database.ManagerAccounts.AsNoTracking().Where(m => m.Sleeve.FundId == fundId).OrderBy(m => m.Id).ToListAsync(cancellationToken);
        Dictionary<int, string> sleeveNames = sleeves.ToDictionary(s => s.Id, s => s.Name);

        switch (scope)
        {
            case PerformanceScope.Fund:
                return new Resolved(
                    new ScopeDto(scope, fund.Id, fund.Name, fund.Id, fund.Name, fund.IsDemo, null, null),
                    fund.BenchmarkId,
                    managers,
                    managers,
                    sleeves.Select(s => (PerformanceScope.Sleeve, s.Id, s.Name, managers.Where(m => m.SleeveId == s.Id).Select(m => m.Id).ToArray())).ToList(),
                    sleeveNames
                );
            case PerformanceScope.Sleeve:
            {
                Sleeve sleeve = sleeves.Single(s => s.Id == id);
                List<ManagerAccount> inSleeve = managers.Where(m => m.SleeveId == id).ToList();
                return new Resolved(
                    new ScopeDto(scope, sleeve.Id, sleeve.Name, fund.Id, fund.Name, fund.IsDemo, sleeve.Id, sleeve.Name),
                    sleeve.BenchmarkId ?? fund.BenchmarkId,
                    managers,
                    inSleeve,
                    inSleeve.Select(m => (PerformanceScope.Manager, m.Id, m.Name, new[] { m.Id })).ToList(),
                    sleeveNames
                );
            }

            default:
            {
                ManagerAccount manager = managers.Single(m => m.Id == id);
                Sleeve sleeve = sleeves.Single(s => s.Id == manager.SleeveId);
                return new Resolved(
                    new ScopeDto(scope, manager.Id, manager.Name, fund.Id, fund.Name, fund.IsDemo, sleeve.Id, sleeve.Name),
                    sleeve.BenchmarkId ?? fund.BenchmarkId,
                    managers,
                    [manager],
                    [],
                    sleeveNames
                );
            }
        }
    }

    private static DateOnly PeriodStart(PerformancePeriod period, DateOnly first, DateOnly asOf)
    {
        DateOnly wanted = period switch
        {
            PerformancePeriod.Ytd => new DateOnly(asOf.Year, 1, 1).AddDays(-1),
            PerformancePeriod.Days30 => asOf.AddDays(-30),
            PerformancePeriod.Days90 => asOf.AddDays(-90),
            _ => first,
        };
        return wanted < first ? first : wanted;
    }

    // ---------------------------------------------------------------- benchmark

    /// <summary>One benchmark return per day after the first of <paramref name="dates"/>.</summary>
    private async Task<double[]> BenchmarkReturnsAsync(Benchmark benchmark, List<DateOnly> dates, CancellationToken cancellationToken)
    {
        double[] result = new double[Math.Max(0, dates.Count - 1)];
        if (benchmark.Kind == BenchmarkKind.FixedRate)
        {
            double daily = Math.Pow(1.0 + (double)(benchmark.FixedAnnualRate ?? 0m), 1.0 / BacktestConstants.DaysPerYear) - 1.0;
            Array.Fill(result, daily);
            return result;
        }

        decimal totalWeight = benchmark.Constituents.Sum(c => c.Weight);
        if (totalWeight <= 0m || dates.Count < 2)
        {
            return result;
        }

        int[] assetIds = benchmark.Constituents.Select(c => c.AssetId).ToArray();
        // A few days before the first date, so the first day has a previous close even after a gap in the feed.
        DateOnly from = dates[0].AddDays(-7);
        DateOnly to = dates[^1];
        var bars = await database
            .PriceBars.AsNoTracking()
            .Where(b => assetIds.Contains(b.AssetId) && b.Date >= from && b.Date <= to)
            .Select(b => new { b.AssetId, b.Date, b.Close })
            .ToListAsync(cancellationToken);
        var byAsset = bars.GroupBy(b => b.AssetId).ToDictionary(g => g.Key, g => new SortedDictionary<DateOnly, decimal>(g.ToDictionary(b => b.Date, b => b.Close)));

        // An asset with no prices at all cannot be part of the basket; the others share its weight, as they would in
        // a real basket that could not buy it. Leaving the weight unfilled would turn part of the benchmark into cash.
        totalWeight = benchmark.Constituents.Where(c => byAsset.ContainsKey(c.AssetId)).Sum(c => c.Weight);
        if (totalWeight <= 0m)
        {
            return result;
        }

        foreach (BenchmarkConstituent constituent in benchmark.Constituents)
        {
            if (!byAsset.TryGetValue(constituent.AssetId, out SortedDictionary<DateOnly, decimal>? prices))
            {
                continue;
            }

            // The basket is rebalanced to its weights every day: its return is the weighted sum of its assets' returns.
            double weight = (double)(constituent.Weight / totalWeight);
            decimal? previous = PriceOnOrBefore(prices, dates[0]);
            for (int t = 1; t < dates.Count; t++)
            {
                decimal? current = PriceOnOrBefore(prices, dates[t]);
                if (previous is > 0m && current is > 0m)
                {
                    result[t - 1] += weight * ((double)(current.Value / previous.Value) - 1.0);
                }

                previous = current ?? previous;
            }
        }

        return result;
    }

    private static decimal? PriceOnOrBefore(SortedDictionary<DateOnly, decimal> prices, DateOnly day)
    {
        if (prices.TryGetValue(day, out decimal exact))
        {
            return exact;
        }

        // Missing day: the last close before it, at most a week old.
        for (int back = 1; back <= 7; back++)
        {
            if (prices.TryGetValue(day.AddDays(-back), out decimal earlier))
            {
                return earlier;
            }
        }

        return null;
    }

    // ---------------------------------------------------------------- statistics

    private static StatsDto Stats(double[] returns)
    {
        BacktestMetrics metrics = BacktestMetricsCalculator.Compute(ReturnCalculator.GrowthIndex(returns));
        return new StatsDto(
            ReturnCalculator.Compound(returns),
            (double?)metrics.Volatility,
            (double?)metrics.Sharpe,
            (double?)metrics.Sortino,
            (double?)metrics.MaxDrawdown,
            (double?)metrics.Calmar
        );
    }

    private static ComparisonDto Compare(double[] portfolio, double[] benchmark)
    {
        int n = Math.Min(portfolio.Length, benchmark.Length);
        BenchmarkComparison c = BenchmarkComparison.Compute(portfolio[..n], benchmark[..n]);
        return new ComparisonDto(c.ExcessReturn, c.Beta, c.Correlation, c.TrackingError, c.InformationRatio);
    }

    private static List<PeriodReturnDto> Periods(List<DateOnly> dates, double[] returns, double[] benchmark, DateOnly asOf)
    {
        DateOnly quarterStart = new(asOf.Year, (asOf.Month - 1) / 3 * 3 + 1, 1);
        (string Label, DateOnly After)[] windows =
        [
            ("1 day", asOf.AddDays(-1)),
            ("7 days", asOf.AddDays(-7)),
            ("30 days", asOf.AddDays(-30)),
            ("Month to date", new DateOnly(asOf.Year, asOf.Month, 1).AddDays(-1)),
            ("Quarter to date", quarterStart.AddDays(-1)),
            ("Year to date", new DateOnly(asOf.Year, 1, 1).AddDays(-1)),
            ("Since inception", dates[0]),
        ];

        List<PeriodReturnDto> result = [];
        foreach ((string label, DateOnly after) in windows)
        {
            // returns[i] is the return of dates[i + 1]. A window that begins before the history does has no value; one that
            // begins on the eve of the first valued day (a fund started on 1 January, year to date) covers the whole history.
            int start = Math.Max(0, dates.FindIndex(d => d >= after));
            bool reaches = after >= dates[0].AddDays(-1);
            result.Add(new PeriodReturnDto(label, reaches ? ReturnCalculator.Compound(returns[start..]) : null, reaches ? ReturnCalculator.Compound(benchmark[start..]) : null));
        }

        return result;
    }

    private static List<MonthlyReturnDto> Monthly(List<DateOnly> dates, double[] returns, double[] benchmark) =>
        Enumerable
            .Range(0, returns.Length)
            .GroupBy(i => new { dates[i + 1].Year, dates[i + 1].Month })
            .OrderBy(g => g.Key.Year)
            .ThenBy(g => g.Key.Month)
            .Select(g => new MonthlyReturnDto($"{g.Key.Year:D4}-{g.Key.Month:D2}", ReturnCalculator.Compound(g.Select(i => returns[i])), ReturnCalculator.Compound(g.Select(i => benchmark[i]))))
            .ToList();

    private static BenchmarkDto Dto(Benchmark benchmark) => new(benchmark.Id, benchmark.Name, benchmark.Kind, benchmark.IsApproximation);

    // ---------------------------------------------------------------- attribution

    private async Task<AttributionDto> AttributeAsync(Resolved resolved, int[] managerIds, DateOnly from, DateOnly asOf, NavSeries series, int start, CancellationToken cancellationToken)
    {
        var positions = await database
            .PositionSnapshots.AsNoTracking()
            .Where(p => managerIds.Contains(p.ManagerAccountId) && p.Date >= from && p.Date <= asOf)
            .Select(p => new
            {
                p.ManagerAccountId,
                p.InstrumentId,
                Asset = p.Instrument.Asset.Symbol,
                p.Instrument.Type,
                p.Instrument.Multiplier,
                p.Date,
                p.Quantity,
                p.MarkPrice,
            })
            .ToListAsync(cancellationToken);

        Dictionary<(int Manager, int Instrument), int> keys = [];
        List<(int Manager, string Asset, InstrumentType Type)> info = [];
        List<AttributionRow> rows = new(positions.Count);
        foreach (var p in positions)
        {
            if (!keys.TryGetValue((p.ManagerAccountId, p.InstrumentId), out int key))
            {
                key = info.Count;
                keys[(p.ManagerAccountId, p.InstrumentId)] = key;
                info.Add((p.ManagerAccountId, p.Asset, p.Type));
            }

            rows.Add(new AttributionRow(key, p.Date, p.Quantity, p.Multiplier, p.MarkPrice, p.Type == InstrumentType.Cash));
        }

        IReadOnlyDictionary<int, decimal> pnl = PnlAttribution.ByPosition(rows);
        Dictionary<int, int> sleeveOfManager = resolved.FundManagers.ToDictionary(m => m.Id, m => m.SleeveId);
        Dictionary<int, string> managerNames = resolved.FundManagers.ToDictionary(m => m.Id, m => m.Name);

        List<PnlLineDto> Group(Func<(int Manager, string Asset, InstrumentType Type), string> by) =>
            pnl.GroupBy(p => by(info[p.Key])).Select(g => new PnlLineDto(g.Key, Math.Round(g.Sum(p => p.Value), 2))).OrderByDescending(l => l.PnlUsd).ToList();

        List<PnlLineDto> byChild = resolved.Scope.Kind switch
        {
            PerformanceScope.Fund => Group(i => resolved.SleeveNames[sleeveOfManager[i.Manager]]),
            PerformanceScope.Sleeve => Group(i => managerNames[i.Manager]),
            _ => [],
        };

        decimal startNav = series.Navs[start];
        decimal flowsInPeriod = series.Flows.Skip(start + 1).Sum();
        return new AttributionDto(
            startNav,
            Math.Round(series.Navs[^1] - startNav - flowsInPeriod, 2),
            Math.Round(pnl.Values.Sum(), 2),
            Group(i => i.Asset),
            Group(i => i.Type.ToString()),
            byChild
        );
    }
}
