using FluentAssertions;
using KatalepsisFT.Application.Prices;
using KatalepsisFT.Infrastructure.MarketData;

namespace KatalepsisFT.Infrastructure.Tests;

public class YahooDailyPriceSourceTests
{
    private static readonly DateOnly From = new(2026, 10, 21);
    private static readonly DateOnly To = new(2026, 10, 24);

    /// <summary>The first three days are copied from a real answer for COIN; the fourth is a day without a close.</summary>
    private const string Answer = """
        {"chart":{"result":[{"meta":{"currency":"USD","symbol":"COIN"},
          "timestamp":[1792589400,1792675800,1792762200,1792848600],
          "indicators":{"quote":[{"close":[177.0800018310547,169.08999633789062,164.4499969482422,null],"volume":[6741500,6584700,6595200,null]}]}}],"error":null}}
        """;

    [Fact]
    public void Parse_GivesCloseAndDollarVolumePerTradingDay_LeavingOutDaysWithoutAClose()
    {
        IReadOnlyList<DailyBar>? bars = YahooDailyPriceSource.Parse(Answer, From, To);

        bars.Should().HaveCount(3);
        bars![0].Should().Be(new DailyBar(new DateOnly(2026, 10, 21), 177.08000183m, Math.Round(6_741_500m * 177.08000183m, 2)));
        bars.Select(b => b.Date).Should().Equal(new DateOnly(2026, 10, 21), new DateOnly(2026, 10, 22), new DateOnly(2026, 10, 23));
    }

    [Fact]
    public void Parse_KeepsOnlyTheDaysAskedFor()
    {
        YahooDailyPriceSource.Parse(Answer, new DateOnly(2026, 10, 22), new DateOnly(2026, 10, 22))!.Select(b => b.Date).Should().Equal(new DateOnly(2026, 10, 22));
    }

    [Fact]
    public void Parse_OfAnUnknownSymbol_IsNull_AndOfAQuietPeriod_IsEmpty()
    {
        YahooDailyPriceSource.Parse("""{"chart":{"result":null,"error":{"code":"Not Found","description":"No data found, symbol may be delisted"}}}""", From, To).Should().BeNull();
        YahooDailyPriceSource.Parse("""{"chart":{"result":[{"meta":{"symbol":"COIN"},"indicators":{"quote":[{}]}}],"error":null}}""", From, To).Should().BeEmpty();
    }

    /// <summary>Against the real service. Off unless LIVE_VENUE_CHECKS=1.</summary>
    [Fact]
    public async Task Live_KnownShareHasBars_AndAnUnknownOneHasNone()
    {
        if (Environment.GetEnvironmentVariable("LIVE_VENUE_CHECKS") != "1")
        {
            return;
        }

        YahooDailyPriceSource source = new(new PlainFactory());
        DateOnly yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);

        IReadOnlyList<DailyBar>? apple = await source.GetDailyBarsAsync("AAPL", yesterday.AddDays(-30), yesterday, CancellationToken.None);
        IReadOnlyList<DailyBar>? classB = await source.GetDailyBarsAsync("BRK.B", yesterday.AddDays(-10), yesterday, CancellationToken.None);
        IReadOnlyList<DailyBar>? unknown = await source.GetDailyBarsAsync("NOSUCHTICKERXYZ", yesterday.AddDays(-10), yesterday, CancellationToken.None);

        apple.Should().NotBeNull();
        apple!.Count.Should().BeInRange(15, 23);
        apple.Should().OnlyContain(b => b.Close > 0m && b.VolumeUsd > 0m && b.Date <= yesterday);
        classB.Should().NotBeNullOrEmpty();
        unknown.Should().BeNull();
    }

    private sealed class PlainFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromSeconds(20) };
    }
}
