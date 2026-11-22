using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using KatalepsisFT.Application.Auth;
using KatalepsisFT.Application.Holdings;
using KatalepsisFT.Application.Performance;
using KatalepsisFT.Domain.Access;
using KatalepsisFT.Web.Tests.Demo;
using Microsoft.AspNetCore.Mvc.Testing;

namespace KatalepsisFT.Web.Tests.Funds;

/// <summary>
/// On the fake prices every asset follows the same path: 82.0 on 1 January 2026, 79.0 on 1 March 2026. So an asset, and
/// with it the BTC benchmark, returns 79/82 − 1 since the fund began.
/// </summary>
public class PerformanceApiTests(TestDatabase database) : IClassFixture<TestDatabase>, IAsyncLifetime
{
    private const double AssetReturn = 79.0 / 82.0 - 1.0;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private HttpClient _client = null!;
    private FundOverviewDto _overview = null!;

    public async Task InitializeAsync()
    {
        WebApplicationFactory<Program> app = database.App;
        if (!await database.IsSeededAsync(app))
        {
            await DemoTestData.SeedAsync(app);
        }

        string email = await database.AddUserAsync(app, Roles.Viewer);
        _client = TestDatabase.CreateClient(app);
        await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, TestDatabase.Password, false));
        int fundId = (await Get<List<FundListItemDto>>("/api/funds")).Single().Id;
        _overview = await Get<FundOverviewDto>($"/api/funds/{fundId}/overview");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<T> Get<T>(string url) => (await _client.GetFromJsonAsync<T>(url, Json))!;

    private int SleeveId(string name) => _overview.Sleeves.Single(s => s.Sleeve.Name == name).Sleeve.Id;

    private async Task<int> ManagerId(string sleeve, string manager) =>
        (await Get<SleeveDetailDto>($"/api/sleeves/{SleeveId(sleeve)}")).Managers.Single(m => m.Manager.Name == manager).Manager.Id;

    [Fact]
    public async Task Fund_ReturnIsNavOverStartingCapital_AndBenchmarkIsTheAssetReturn()
    {
        PerformanceDto p = await Get<PerformanceDto>($"/api/performance/Fund/{_overview.Fund.Id}");

        p.Scope.Name.Should().Be("Liquid Master Fund");
        p.Benchmark.Name.Should().Be("BTC");
        p.From.Should().Be(new DateOnly(2026, 1, 1));
        p.AsOf.Should().Be(DemoTestData.LastDay);
        p.Stats.TotalReturn.Should().BeApproximately((double)(_overview.Totals.NavUsd / 250_000_000m) - 1.0, 1e-9);
        p.BenchmarkStats.TotalReturn.Should().BeApproximately(AssetReturn, 1e-9);
        p.Comparison.ExcessReturn.Should().BeApproximately(p.Stats.TotalReturn - AssetReturn, 1e-9);
        p.Dates.Should().HaveCount(60).And.BeInAscendingOrder();
        p.Portfolio[0].Should().Be(0.0);
        p.Portfolio[^1].Should().BeApproximately(p.Stats.TotalReturn, 1e-9);
        p.Drawdown.Min().Should().BeApproximately(p.Stats.MaxDrawdown!.Value, 1e-6);
        p.Benchmarks.Select(b => b.Name).Should().BeEquivalentTo(["BTC", "ETH", "Cash (0%)"]);
    }

    [Fact]
    public async Task Fund_ChildrenAreItsSleeves_AndAttributionExplainsTheNavChange()
    {
        PerformanceDto p = await Get<PerformanceDto>($"/api/performance/Fund/{_overview.Fund.Id}");

        p.Children.Select(c => c.Name).Should().Equal("Beta", "Market Neutral", "Directional");
        p.Children.Sum(c => c.NavUsd).Should().Be(_overview.Totals.NavUsd);
        p.Attribution.StartNavUsd.Should().Be(250_000_000m);
        p.Attribution.NavChangeUsd.Should().Be(_overview.Totals.NavUsd - 250_000_000m);
        // Books are held unchanged, so price moves explain the whole change, up to the cents rounded on each valued row.
        p.Attribution.ExplainedUsd.Should().BeApproximately(p.Attribution.NavChangeUsd, 100m);
        p.Attribution.ByChild.Sum(l => l.PnlUsd).Should().BeApproximately(p.Attribution.ExplainedUsd, 1m);
        p.Attribution.ByAsset.Sum(l => l.PnlUsd).Should().BeApproximately(p.Attribution.ExplainedUsd, 1m);
        p.Attribution.ByType.Select(l => l.Key).Should().BeEquivalentTo(["Spot", "Perp"]);
        p.Attribution.ByType.Single(l => l.Key == "Perp").PnlUsd.Should().BePositive("the fund is net short perps and prices fell");
    }

    [Fact]
    public async Task Fund_PeriodsAndMonths_CompoundToTheTotal()
    {
        PerformanceDto p = await Get<PerformanceDto>($"/api/performance/Fund/{_overview.Fund.Id}");

        p.Periods.Single(x => x.Label == "Since inception").Portfolio.Should().BeApproximately(p.Stats.TotalReturn, 1e-9);
        p.Periods.Single(x => x.Label == "Year to date").Portfolio.Should().BeApproximately(p.Stats.TotalReturn, 1e-9, "the fund began on 1 January");
        p.Periods.Single(x => x.Label == "1 day").Benchmark.Should().NotBeNull();
        p.Monthly.Select(m => m.Month).Should().Equal("2026-01", "2026-02", "2026-03");
        p.Monthly.Aggregate(1.0, (level, m) => level * (1 + m.Portfolio)).Should().BeApproximately(1 + p.Stats.TotalReturn, 1e-9);
        p.Monthly.Aggregate(1.0, (level, m) => level * (1 + m.Benchmark)).Should().BeApproximately(1 + AssetReturn, 1e-9);
    }

    [Fact]
    public async Task HedgedManager_IsFlat_AndHasNoBetaToSpeakOf()
    {
        PerformanceDto p = await Get<PerformanceDto>($"/api/performance/Manager/{await ManagerId("Market Neutral", "Tessera Basis")}");

        p.Stats.TotalReturn.Should().BeApproximately(0.0, 1e-6);
        Math.Abs(p.Comparison.Beta ?? 0.0).Should().BeLessThan(0.001);
        p.Stats.Sharpe.Should().BeNull("a flat series has no Sharpe ratio");
        p.Children.Should().BeEmpty();
        p.Scope.SleeveName.Should().Be("Market Neutral");
    }

    [Fact]
    public async Task LongOnlyManager_MovesWithTheBenchmark()
    {
        PerformanceDto p = await Get<PerformanceDto>($"/api/performance/Manager/{await ManagerId("Beta", "Northgate Index")}?benchmarkId={BenchmarkId(await Benchmarks(), "BTC")}");

        // 94% of its capital went into assets that all returned the same.
        p.Stats.TotalReturn.Should().BeApproximately(0.94 * AssetReturn, 1e-6);
        p.Comparison.Correlation!.Value.Should().BeGreaterThan(0.999);
        p.Comparison.Beta!.Value.Should().BeInRange(0.90, 0.95);
        p.Stats.MaxDrawdown.Should().BeNegative();
    }

    [Fact]
    public async Task Sleeve_MarketNeutral_IsMeasuredAgainstCashByDefault_AndTheBenchmarkCanBeChanged()
    {
        int sleeveId = SleeveId("Market Neutral");

        PerformanceDto byDefault = await Get<PerformanceDto>($"/api/performance/Sleeve/{sleeveId}");
        PerformanceDto againstEth = await Get<PerformanceDto>($"/api/performance/Sleeve/{sleeveId}?benchmarkId={BenchmarkId(byDefault.Benchmarks, "ETH")}");

        byDefault.Benchmark.Name.Should().Be("Cash (0%)");
        byDefault.BenchmarkStats.TotalReturn.Should().Be(0.0);
        byDefault.Comparison.Beta.Should().BeNull("cash does not move");
        byDefault.Children.Should().HaveCount(3).And.OnlyContain(c => c.Kind == PerformanceScope.Manager);
        againstEth.Benchmark.Name.Should().Be("ETH");
        againstEth.BenchmarkStats.TotalReturn.Should().BeApproximately(AssetReturn, 1e-9);
    }

    [Fact]
    public async Task Period_Last30Days_StartsThirtyDaysBack()
    {
        PerformanceDto p = await Get<PerformanceDto>($"/api/performance/Fund/{_overview.Fund.Id}?period=Days30");

        p.From.Should().Be(DemoTestData.LastDay.AddDays(-30));
        p.Dates.Should().HaveCount(31);
        // 79.0 on the last day against 80.5 thirty days earlier.
        p.BenchmarkStats.TotalReturn.Should().BeApproximately(79.0 / 80.5 - 1.0, 1e-9);
    }

    [Fact]
    public async Task Asset_AgainstABenchmarkOnTheSamePath_HasBetaOneAndNoExcess()
    {
        AssetPerformanceDto p = await Get<AssetPerformanceDto>("/api/performance/asset/ondo");

        p.Symbol.Should().Be("ONDO");
        p.Benchmark.Name.Should().Be("BTC");
        p.Comparison.ExcessReturn.Should().BeApproximately(0.0, 1e-12);
        p.Comparison.Beta!.Value.Should().BeApproximately(1.0, 1e-9);
        p.Asset[^1].Should().BeApproximately(p.Stats.TotalReturn, 1e-9);
    }

    [Fact]
    public async Task UnknownScopeOrAsset_Returns404_AndNoSession_Returns401()
    {
        (await _client.GetAsync("/api/performance/Fund/999999")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.GetAsync("/api/performance/Sleeve/999999")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.GetAsync("/api/performance/asset/NOPE")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await TestDatabase.CreateClient(database.App).GetAsync($"/api/performance/Fund/{_overview.Fund.Id}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<IReadOnlyList<BenchmarkDto>> Benchmarks() => (await Get<PerformanceDto>($"/api/performance/Fund/{_overview.Fund.Id}")).Benchmarks;

    private static int BenchmarkId(IReadOnlyList<BenchmarkDto> benchmarks, string name) => benchmarks.Single(b => b.Name == name).Id;
}
