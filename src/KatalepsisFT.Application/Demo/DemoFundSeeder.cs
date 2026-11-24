using KatalepsisFT.Domain.Benchmarks;
using KatalepsisFT.Domain.Funds;
using KatalepsisFT.Domain.Instruments;
using KatalepsisFT.Domain.Positions;
using Microsoft.EntityFrameworkCore;

namespace KatalepsisFT.Application.Demo;

public sealed record DemoSeedResult(bool FundCreated, int DaysAdded, int PositionRowsAdded, string? WaitingFor);

/// <summary>
/// Creates the demo fund once and then keeps it current: every run adds the days for which prices have arrived since
/// the last one. Books are held unchanged, so the same run on the same prices always writes the same rows.
/// </summary>
public sealed class DemoFundSeeder(IFtDbContext database)
{
    /// <summary>The assets the demo needs. Runs before prices are loaded, so the price backfill knows what to fetch.</summary>
    public async Task EnsureAssetsAsync(CancellationToken cancellationToken)
    {
        HashSet<string> existing = (await database.Assets.Select(a => a.Symbol).ToListAsync(cancellationToken)).ToHashSet();
        foreach ((string symbol, string name) in DemoFundDefinition.CryptoAssets.Where(a => !existing.Contains(a.Symbol)))
        {
            database.Assets.Add(new Asset { Symbol = symbol, Name = name, AssetClass = AssetClass.Crypto });
        }

        if (!existing.Contains(DemoFundDefinition.CashAsset))
        {
            database.Assets.Add(new Asset { Symbol = DemoFundDefinition.CashAsset, Name = "USD Coin", AssetClass = AssetClass.Cash });
        }

        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task<DemoSeedResult> EnsureFundAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, Asset> assets = await database.Assets.ToDictionaryAsync(a => a.Symbol, cancellationToken);
        string[] needed = DemoFundDefinition.CryptoAssets.Select(a => a.Symbol).ToArray();
        int[] neededIds = needed.Select(s => assets[s].Id).ToArray();

        // prices[asset][date], from the day before inception so every book can start on the inception day.
        var bars = await database
            .PriceBars.Where(b => neededIds.Contains(b.AssetId) && b.Date >= DemoFundDefinition.Inception.AddDays(-DemoFundDefinition.AdvWindowDays))
            .Select(b => new { b.Asset.Symbol, b.Date, b.Close, b.VolumeUsd })
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        Dictionary<string, SortedDictionary<DateOnly, decimal>> prices = needed.ToDictionary(s => s, _ => new SortedDictionary<DateOnly, decimal>());
        foreach (var bar in bars)
        {
            prices[bar.Symbol][bar.Date] = bar.Close;
        }

        string[] noInceptionPrice = needed.Where(s => !prices[s].ContainsKey(DemoFundDefinition.Inception)).ToArray();
        if (noInceptionPrice.Length > 0)
        {
            return new DemoSeedResult(false, 0, 0, $"prices on {DemoFundDefinition.Inception:yyyy-MM-dd} for {string.Join(", ", noInceptionPrice)}");
        }

        // Only days on which every asset has a price are valued, so a late feed delays the demo instead of distorting it.
        DateOnly lastCommonDay = needed.Min(s => prices[s].Keys.Max());
        Fund? fund = await database.Funds.FirstOrDefaultAsync(f => f.IsDemo && f.Name == DemoFundDefinition.FundName, cancellationToken);
        bool created = fund is null;
        if (fund is null)
        {
            fund = await CreateStructureAsync(assets, cancellationToken);
        }

        await EnsureBenchmarksAsync(fund, assets, cancellationToken);

        List<ManagerAccount> managers = await database.ManagerAccounts.Where(m => m.Sleeve.FundId == fund.Id).ToListAsync(cancellationToken);
        Dictionary<(string Asset, InstrumentType Type), Instrument> instruments = await database
            // Only the kinds the demo fund holds. Real funds may hold several options or futures of one asset, and
            // those would be two entries under one key.
            .Instruments.Include(i => i.Asset)
            .Where(i => i.Type == InstrumentType.Spot || i.Type == InstrumentType.Perp || i.Type == InstrumentType.Cash)
            .ToDictionaryAsync(i => (i.Asset.Symbol, i.Type), cancellationToken);
        Dictionary<int, DateOnly> lastSnapshot = await database
            .NavSnapshots.Where(n => n.ManagerAccount.Sleeve.FundId == fund.Id)
            .GroupBy(n => n.ManagerAccountId)
            .Select(g => new { g.Key, Last = g.Max(n => n.Date) })
            .ToDictionaryAsync(x => x.Key, x => x.Last, cancellationToken);

        decimal crowdedGrossQuantity = lastSnapshot.Count == managers.Count ? 0m : CrowdedGrossQuantity(bars.Where(b => b.Symbol == DemoFundDefinition.CrowdedAsset).Select(b => (b.Date, b.Close, b.VolumeUsd)).ToList());
        Dictionary<string, decimal> inceptionPrices = needed.ToDictionary(s => s, s => prices[s][DemoFundDefinition.Inception]);
        Dictionary<string, DemoManager> definitions = DemoFundDefinition.Sleeves.SelectMany(s => s.Managers).ToDictionary(m => m.Name);

        int daysAdded = 0, rowsAdded = 0;
        foreach (ManagerAccount manager in managers)
        {
            IReadOnlyList<DemoLine> lines;
            DateOnly from;
            if (lastSnapshot.TryGetValue(manager.Id, out DateOnly last))
            {
                // Carry yesterday's book forward: same quantities, same entry prices.
                lines = await database
                    .PositionSnapshots.Where(p => p.ManagerAccountId == manager.Id && p.Date == last)
                    .Select(p => new DemoLine(p.Instrument.Asset.Symbol, p.Instrument.Type, p.Quantity, p.EntryPrice ?? 1m))
                    .ToListAsync(cancellationToken);
                from = last.AddDays(1);
            }
            else
            {
                lines = DemoBookBuilder.Start(definitions[manager.Name], inceptionPrices, crowdedGrossQuantity);
                from = DemoFundDefinition.Inception;
            }

            int managerDays = 0;
            for (DateOnly day = from; day <= lastCommonDay; day = day.AddDays(1))
            {
                Dictionary<string, decimal>? dayPrices = PricesOn(prices, needed, day);
                if (dayPrices is null)
                {
                    continue;
                }

                IReadOnlyList<DemoValuedLine> valued = DemoBookBuilder.Value(lines, dayPrices);
                foreach (DemoValuedLine v in valued)
                {
                    database.PositionSnapshots.Add(
                        new PositionSnapshot
                        {
                            ManagerAccountId = manager.Id,
                            InstrumentId = instruments[(v.Line.Asset, v.Line.Type)].Id,
                            Date = day,
                            Quantity = v.Line.Quantity,
                            EntryPrice = v.Line.EntryPrice,
                            MarkPrice = v.MarkPrice,
                            UnderlyingPrice = v.MarkPrice,
                            MarketValueUsd = v.Value.MarketValueUsd,
                            ExposureUsd = v.Value.ExposureUsd,
                            UnrealizedPnlUsd = v.Value.UnrealizedPnlUsd,
                        }
                    );
                    rowsAdded++;
                }

                database.NavSnapshots.Add(new NavSnapshot { ManagerAccountId = manager.Id, Date = day, NavUsd = valued.Sum(v => v.Value.MarketValueUsd) });
                managerDays++;
            }

            await database.SaveChangesAsync(cancellationToken);
            daysAdded = Math.Max(daysAdded, managerDays);
        }

        return new DemoSeedResult(created, daysAdded, rowsAdded, null);
    }

    /// <summary>
    /// The other benchmarks the demo offers, and the market-neutral sleeve measured against cash. Runs on every pass so
    /// that a fund created by an earlier version gets them too.
    /// </summary>
    private async Task EnsureBenchmarksAsync(Fund fund, Dictionary<string, Asset> assets, CancellationToken cancellationToken)
    {
        HashSet<string> existing = (await database.Benchmarks.Select(b => b.Name).ToListAsync(cancellationToken)).ToHashSet();
        if (!existing.Contains(DemoFundDefinition.EthBenchmarkName))
        {
            database.Benchmarks.Add(
                new Benchmark
                {
                    Name = DemoFundDefinition.EthBenchmarkName,
                    Kind = BenchmarkKind.SingleAsset,
                    Constituents = [new BenchmarkConstituent { AssetId = assets["ETH"].Id, Weight = 1m }],
                }
            );
        }

        if (!existing.Contains(DemoFundDefinition.CashBenchmarkName))
        {
            database.Benchmarks.Add(new Benchmark { Name = DemoFundDefinition.CashBenchmarkName, Kind = BenchmarkKind.FixedRate, FixedAnnualRate = 0m });
        }

        await database.SaveChangesAsync(cancellationToken);

        Sleeve? neutral = await database.Sleeves.FirstOrDefaultAsync(s => s.FundId == fund.Id && s.MandateType == MandateType.MarketNeutral && s.BenchmarkId == null, cancellationToken);
        if (neutral is not null)
        {
            neutral.BenchmarkId = await database.Benchmarks.Where(b => b.Name == DemoFundDefinition.CashBenchmarkName).Select(b => b.Id).FirstAsync(cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>Fund-wide quantity of the crowded asset: a multiple of its recent average daily volume, at the latest price.</summary>
    private static decimal CrowdedGrossQuantity(List<(DateOnly Date, decimal Close, decimal VolumeUsd)> crowdedBars)
    {
        List<(DateOnly Date, decimal Close, decimal VolumeUsd)> recent = crowdedBars.OrderByDescending(b => b.Date).Take(DemoFundDefinition.AdvWindowDays).ToList();
        decimal averageVolume = recent.Average(b => b.VolumeUsd);
        return Math.Round(DemoFundDefinition.CrowdedAdvMultiple * averageVolume / recent[0].Close, 8);
    }

    private static Dictionary<string, decimal>? PricesOn(Dictionary<string, SortedDictionary<DateOnly, decimal>> prices, string[] assets, DateOnly day)
    {
        Dictionary<string, decimal> result = new(assets.Length);
        foreach (string asset in assets)
        {
            if (!prices[asset].TryGetValue(day, out decimal price))
            {
                return null;
            }

            result[asset] = price;
        }

        return result;
    }

    private async Task<Fund> CreateStructureAsync(Dictionary<string, Asset> assets, CancellationToken cancellationToken)
    {
        Benchmark benchmark =
            await database.Benchmarks.FirstOrDefaultAsync(b => b.Name == DemoFundDefinition.BenchmarkName, cancellationToken)
            ?? database
                .Benchmarks.Add(
                    new Benchmark
                    {
                        Name = DemoFundDefinition.BenchmarkName,
                        Kind = BenchmarkKind.SingleAsset,
                        Constituents = [new BenchmarkConstituent { AssetId = assets[DemoFundDefinition.BenchmarkName].Id, Weight = 1m }],
                    }
                )
                .Entity;

        HashSet<(int AssetId, InstrumentType Type)> existing = (await database.Instruments.Select(i => new { i.AssetId, i.Type }).ToListAsync(cancellationToken))
            .Select(i => (i.AssetId, i.Type))
            .ToHashSet();
        foreach ((string symbol, _) in DemoFundDefinition.CryptoAssets)
        {
            if (!existing.Contains((assets[symbol].Id, InstrumentType.Spot)))
            {
                database.Instruments.Add(new Instrument { AssetId = assets[symbol].Id, Type = InstrumentType.Spot, Symbol = symbol });
            }

            if (!existing.Contains((assets[symbol].Id, InstrumentType.Perp)))
            {
                database.Instruments.Add(new Instrument { AssetId = assets[symbol].Id, Type = InstrumentType.Perp, Symbol = symbol + "-PERP" });
            }
        }

        int cashId = assets[DemoFundDefinition.CashAsset].Id;
        if (!existing.Contains((cashId, InstrumentType.Cash)))
        {
            database.Instruments.Add(new Instrument { AssetId = cashId, Type = InstrumentType.Cash, Symbol = DemoFundDefinition.CashAsset });
        }

        Fund fund = new()
        {
            Name = DemoFundDefinition.FundName,
            IsDemo = true,
            Benchmark = benchmark,
            CreatedAt = DateTimeOffset.UtcNow,
            Sleeves = DemoFundDefinition
                .Sleeves.Select(
                    (sleeve, index) =>
                        new Sleeve
                        {
                            Name = sleeve.Name,
                            MandateType = sleeve.MandateType,
                            SortOrder = index,
                            Limits = sleeve.Limits.Select(l => new MandateLimit { Kind = l.Kind, Value = l.Value }).ToList(),
                            Managers = sleeve
                                .Managers.Select(m => new ManagerAccount
                                {
                                    Name = m.Name,
                                    InceptionDate = DemoFundDefinition.Inception,
                                    AllocatedCapitalUsd = m.CapitalUsd,
                                    DataSource = ManagerDataSource.Demo,
                                })
                                .ToList(),
                        }
                )
                .ToList(),
        };
        database.Funds.Add(fund);
        await database.SaveChangesAsync(cancellationToken);
        return fund;
    }
}
