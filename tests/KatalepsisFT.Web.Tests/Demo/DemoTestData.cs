using KatalepsisFT.Application.Demo;
using KatalepsisFT.Application.Prices;
using KatalepsisFT.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace KatalepsisFT.Web.Tests.Demo;

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Prices that fall by 0.5 every 10 days from 100, the same for every asset, with a fixed volume.</summary>
internal sealed class FakePrices : IDailyPriceSource
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

internal static class DemoTestData
{
    public static readonly DateOnly LastDay = new(2026, 3, 1);

    /// <summary>Seeds the demo fund on fake prices up to <see cref="LastDay"/>.</summary>
    public static async Task SeedAsync(WebApplicationFactory<Program> app)
    {
        using IServiceScope scope = app.Services.CreateScope();
        FtDbContext db = scope.ServiceProvider.GetRequiredService<FtDbContext>();
        DemoFundSeeder seeder = new(db);
        await seeder.EnsureAssetsAsync(CancellationToken.None);
        FixedClock clock = new(new DateTimeOffset(LastDay.AddDays(1).ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero));
        await new PriceBackfillService(db, [new FakePrices()], clock).RunAsync(DemoFundDefinition.HistoryStart, CancellationToken.None);
        await seeder.EnsureFundAsync(CancellationToken.None);
    }
}
