using FluentAssertions;
using KatalepsisFT.Application.Demo;
using KatalepsisFT.Application.Prices;
using KatalepsisFT.Domain.Instruments;
using KatalepsisFT.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KatalepsisFT.Web.Tests.Demo;

public class DemoSeedingTests(TestDatabase database) : IClassFixture<TestDatabase>
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Prices that fall 1% of their starting value every 10 days, the same for every asset, with a fixed volume.</summary>
    private sealed class FakePrices : IDailyPriceSource
    {
        public HashSet<string> Unknown { get; } = [];
        public List<(string Asset, DateOnly From, DateOnly To)> Requests { get; } = [];

        public string Name => "fake";

        public static decimal PriceOn(DateOnly date) => 100m - (date.DayNumber - DemoFundDefinition.HistoryStart.DayNumber) / 10 * 0.5m;

        public Task<IReadOnlyList<DailyBar>?> GetDailyBarsAsync(string assetSymbol, DateOnly from, DateOnly to, CancellationToken cancellationToken)
        {
            Requests.Add((assetSymbol, from, to));
            if (Unknown.Contains(assetSymbol))
            {
                return Task.FromResult<IReadOnlyList<DailyBar>?>(null);
            }

            List<DailyBar> bars = [];
            for (DateOnly day = from; day <= to; day = day.AddDays(1))
            {
                bars.Add(new DailyBar(day, PriceOn(day), 2_000_000m));
            }

            return Task.FromResult<IReadOnlyList<DailyBar>?>(bars);
        }
    }

    // One scenario in order: each step depends on the rows the previous one left behind.
    [Fact]
    public async Task Backfill_ThenSeed_ThenRollForward()
    {
        using IServiceScope scope = database.CreateApp().Services.CreateScope();
        FtDbContext db = scope.ServiceProvider.GetRequiredService<FtDbContext>();
        FakePrices prices = new();
        Clock clock = new(new DateTimeOffset(2026, 1, 11, 9, 0, 0, TimeSpan.Zero));
        PriceBackfillService backfill = new(db, prices, clock);
        DemoFundSeeder seeder = new(db);

        // Before any price exists the seeder says what it is waiting for and writes nothing.
        await seeder.EnsureAssetsAsync(CancellationToken.None);
        DemoSeedResult early = await seeder.EnsureFundAsync(CancellationToken.None);
        early.WaitingFor.Should().NotBeNull();
        (await db.Funds.CountAsync()).Should().Be(0);

        // An asset the source does not know is reported and does not stop the others.
        prices.Unknown.Add("AVAX");
        PriceBackfillResult first = await backfill.RunAsync(DemoFundDefinition.HistoryStart, CancellationToken.None);
        first.AssetsWithoutData.Should().Equal("AVAX");
        (await seeder.EnsureFundAsync(CancellationToken.None)).WaitingFor.Should().Contain("AVAX");
        prices.Unknown.Clear();

        // Today's bar is incomplete, so history ends yesterday; cash is not a priced asset.
        await backfill.RunAsync(DemoFundDefinition.HistoryStart, CancellationToken.None);
        DateOnly yesterday = new(2026, 1, 10);
        (await db.PriceBars.MaxAsync(b => b.Date)).Should().Be(yesterday);
        prices.Requests.Should().NotContain(r => r.Asset == DemoFundDefinition.CashAsset);
        int barsAfterBackfill = await db.PriceBars.CountAsync();

        // A second run fetches nothing and adds nothing.
        (await backfill.RunAsync(DemoFundDefinition.HistoryStart, CancellationToken.None)).BarsAdded.Should().Be(0);
        (await db.PriceBars.CountAsync()).Should().Be(barsAfterBackfill);

        // Seeding creates the structure and ten days of books; every manager starts at exactly its capital.
        DemoSeedResult seeded = await seeder.EnsureFundAsync(CancellationToken.None);
        seeded.FundCreated.Should().BeTrue();
        seeded.DaysAdded.Should().Be(10);
        (await db.Sleeves.CountAsync()).Should().Be(3);
        (await db.ManagerAccounts.CountAsync()).Should().Be(9);
        var day1 = await db.NavSnapshots.Where(n => n.Date == DemoFundDefinition.Inception).Select(n => new { n.ManagerAccount.AllocatedCapitalUsd, n.NavUsd }).ToListAsync();
        day1.Should().HaveCount(9).And.OnlyContain(n => Math.Abs(n.NavUsd - n.AllocatedCapitalUsd) < 1m);

        // The crowded asset is held by five managers and its gross is 1.6 times its average daily volume.
        var crowded = await db
            .PositionSnapshots.Where(p => p.Date == yesterday && p.Instrument.Asset.Symbol == DemoFundDefinition.CrowdedAsset)
            .Select(p => new { p.ManagerAccountId, p.Quantity, p.Instrument.Type })
            .ToListAsync();
        crowded.Select(p => p.ManagerAccountId).Distinct().Should().HaveCount(5);
        (crowded.Sum(p => Math.Abs(p.Quantity)) * FakePrices.PriceOn(yesterday)).Should().BeApproximately(3_200_000m, 1m);
        crowded.Should().Contain(p => p.Quantity < 0m && p.Type == InstrumentType.Perp);

        // The hedged managers of the market-neutral sleeve do not move with the market.
        var neutral = await db.NavSnapshots.Where(n => n.Date == yesterday && n.ManagerAccount.Name == "Tessera Basis").SingleAsync();
        neutral.NavUsd.Should().BeApproximately(35_000_000m, 1m);

        // Running again writes nothing; two days later exactly two more days are added, with unchanged quantities.
        int rows = await db.PositionSnapshots.CountAsync();
        (await seeder.EnsureFundAsync(CancellationToken.None)).PositionRowsAdded.Should().Be(0);
        clock.Now = clock.Now.AddDays(2);
        await backfill.RunAsync(DemoFundDefinition.HistoryStart, CancellationToken.None);
        DemoSeedResult rolled = await seeder.EnsureFundAsync(CancellationToken.None);
        rolled.FundCreated.Should().BeFalse();
        rolled.DaysAdded.Should().Be(2);
        (await db.PositionSnapshots.CountAsync()).Should().Be(rows + rolled.PositionRowsAdded);
        (await db.PositionSnapshots.Where(p => p.ManagerAccount.Name == "Northgate Index").GroupBy(p => p.InstrumentId).Select(g => g.Select(p => p.Quantity).Distinct().Count()).ToListAsync())
            .Should()
            .OnlyContain(distinctQuantities => distinctQuantities == 1);
        (await db.Funds.CountAsync()).Should().Be(1);
    }
}
