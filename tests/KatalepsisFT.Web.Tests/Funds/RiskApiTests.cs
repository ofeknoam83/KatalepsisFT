using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using KatalepsisFT.Application.Auth;
using KatalepsisFT.Application.Holdings;
using KatalepsisFT.Application.Performance;
using KatalepsisFT.Application.Risk;
using KatalepsisFT.Domain.Access;
using KatalepsisFT.Web.Tests.Demo;
using Microsoft.AspNetCore.Mvc.Testing;

namespace KatalepsisFT.Web.Tests.Funds;

/// <summary>
/// On the fake prices every asset moves exactly like BTC (beta 1) and trades 2,000,000 USD a day, so a BTC shock moves
/// the book by the shock times its net exposure, and an asset needs gross / (participation × 2,000,000) days to sell.
/// </summary>
public class RiskApiTests(TestDatabase database) : IClassFixture<TestDatabase>, IAsyncLifetime
{
    private const decimal DailyVolume = 2_000_000m;
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

    private int FundId => _overview.Fund.Id;

    private int SleeveId(string name) => _overview.Sleeves.Single(s => s.Sleeve.Name == name).Sleeve.Id;

    [Fact]
    public async Task Fund_ScenarioAtBetaOne_IsShockTimesNetExposure()
    {
        RiskDto risk = await Get<RiskDto>($"/api/risk/Fund/{FundId}");

        risk.AsOf.Should().Be(DemoTestData.LastDay);
        risk.Scenario.ReferenceAsset.Should().Be("BTC");
        risk.Assets.Should().OnlyContain(a => !a.BetaIsAssumed && Math.Abs(a.Beta - 1.0) < 1e-6);
        int betaOne = risk.Scenario.BetaMultipliers.ToList().IndexOf(1.0);
        for (int i = 0; i < risk.Scenario.Shocks.Count; i++)
        {
            decimal expected = (decimal)risk.Scenario.Shocks[i] * risk.Totals.NetUsd;
            risk.Scenario.Cells[i][betaOne].Should().BeApproximately(expected, 5m);
        }
    }

    [Fact]
    public async Task Fund_HigherBetaMultiplier_ScalesEverythingButTheReferenceAsset()
    {
        RiskDto risk = await Get<RiskDto>($"/api/risk/Fund/{FundId}");

        decimal btc = risk.Assets.Single(a => a.Asset == "BTC").NetUsd;
        int shock = risk.Scenario.Shocks.ToList().IndexOf(-0.2);
        int high = risk.Scenario.BetaMultipliers.ToList().IndexOf(1.5);
        decimal expected = -0.2m * (btc + 1.5m * (risk.Totals.NetUsd - btc));
        risk.Scenario.Cells[shock][high].Should().BeApproximately(expected, 5m);
    }

    [Fact]
    public async Task Fund_SleeveScenarioRows_AddUpToTheFund()
    {
        RiskDto risk = await Get<RiskDto>($"/api/risk/Fund/{FundId}");

        risk.ScenarioByChild.Select(c => c.Name).Should().Equal("Beta", "Market Neutral", "Directional");
        risk.ScenarioByChild.Should().OnlyContain(c => c.Kind == PerformanceScope.Sleeve);
        int betaOne = risk.Scenario.BetaMultipliers.ToList().IndexOf(1.0);
        for (int i = 0; i < risk.Scenario.Shocks.Count; i++)
        {
            risk.ScenarioByChild.Sum(c => c.Pnl[i]).Should().BeApproximately(risk.Scenario.Cells[i][betaOne], 10m);
        }

        risk.ScenarioByChild.Sum(c => c.NavUsd).Should().Be(risk.Totals.NavUsd);
    }

    [Fact]
    public async Task Fund_ValueAtRisk_IsALossNoLargerThanExpectedShortfall()
    {
        RiskDto risk = await Get<RiskDto>($"/api/risk/Fund/{FundId}");

        risk.Var.Days.Should().Be(365);
        risk.Var.ValueAtRiskUsd.Should().BeGreaterThan(0m);
        risk.Var.ExpectedShortfallUsd.Should().BeGreaterThanOrEqualTo(risk.Var.ValueAtRiskUsd);
        risk.TopFiveShareOfGross.Should().BeInRange(0m, 1m);
    }

    [Fact]
    public async Task Fund_ChecksEverySleeveMandate_AndSleeveOnlyItsOwn()
    {
        RiskDto fund = await Get<RiskDto>($"/api/risk/Fund/{FundId}");
        RiskDto sleeve = await Get<RiskDto>($"/api/risk/Sleeve/{SleeveId("Directional")}");

        fund.Mandate.Select(m => m.Sleeve).Distinct().Should().BeEquivalentTo("Beta", "Market Neutral", "Directional");
        sleeve.Mandate.Should().NotBeEmpty().And.OnlyContain(m => m.Sleeve == "Directional");
        sleeve.Mandate.Should().BeEquivalentTo(fund.Mandate.Where(m => m.Sleeve == "Directional"));
        sleeve.Mandate.Should().OnlyContain(m => m.Current != null && m.WithinLimit != null);
    }

    [Fact]
    public async Task Sleeve_VolatilityAndCorrelation_CoverItsManagers()
    {
        int sleeveId = SleeveId("Directional");
        SleeveDetailDto detail = await Get<SleeveDetailDto>($"/api/sleeves/{sleeveId}");
        RiskDto risk = await Get<RiskDto>($"/api/risk/Sleeve/{sleeveId}");

        List<string> managers = detail.Managers.Select(m => m.Manager.Name).ToList();
        risk.Volatility[0].Name.Should().Be("Directional");
        risk.Volatility.Skip(1).Select(v => v.Name).Should().BeEquivalentTo(managers);
        risk.Correlation.Names.Should().BeEquivalentTo(managers);
        for (int i = 0; i < managers.Count; i++)
        {
            risk.Correlation.Matrix[i][i].Should().Be(1.0);
            risk.Correlation.Matrix[i].Should().HaveCount(managers.Count);
        }

        risk.RollingVolatility.Should().OnlyContain(s => s.Values.Count == risk.RollingDates.Count);
        risk.RollingVolatility.Select(s => s.Name).Should().Contain("BTC");
        risk.RollingDates[^1].Should().Be(DemoTestData.LastDay);
    }

    [Fact]
    public async Task Manager_HasNoChildren()
    {
        int managerId = (await Get<SleeveDetailDto>($"/api/sleeves/{SleeveId("Beta")}")).Managers[0].Manager.Id;

        RiskDto risk = await Get<RiskDto>($"/api/risk/Manager/{managerId}");

        risk.ScenarioByChild.Should().BeEmpty();
        risk.Correlation.Names.Should().HaveCount(1);
        risk.Volatility.Should().HaveCount(1);
    }

    [Fact]
    public async Task Liquidity_DaysAreGrossOverTheShareOfDailyVolume()
    {
        LiquidityDto tenth = await Get<LiquidityDto>($"/api/liquidity/Fund/{FundId}");
        LiquidityDto fifth = await Get<LiquidityDto>($"/api/liquidity/Fund/{FundId}?participation=0.2");

        tenth.Participation.Should().Be(0.10m);
        LiquidityLineDto ondo = tenth.Lines.Single(l => l.Asset == "ONDO");
        ondo.AverageDailyVolumeUsd.Should().Be(DailyVolume);
        ondo.DailyCapacityUsd.Should().Be(DailyVolume * 0.10m);
        ondo.Days.Should().BeApproximately((double)(ondo.GrossUsd / (DailyVolume * 0.10m)), 1e-6);
        fifth.Lines.Single(l => l.Asset == "ONDO").Days.Should().BeApproximately(ondo.Days!.Value / 2.0, 1e-6);
        tenth.Lines.Select(l => l.Days ?? double.MaxValue).Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task Liquidity_LadderBucketsAddUp_AndTheQueueListsHoldersOfTheSlowestAsset()
    {
        LiquidityDto liquidity = await Get<LiquidityDto>($"/api/liquidity/Fund/{FundId}");

        LadderDto l = liquidity.Ladder;
        (l.WithinOneDayUsd + l.OneToFiveDaysUsd + l.FiveToTwentyDaysUsd + l.BeyondTwentyDaysUsd + l.UnknownUsd).Should().BeApproximately(l.TotalUsd, 1m);
        liquidity.Children.Select(c => c.Name).Should().Equal("Beta", "Market Neutral", "Directional");
        liquidity.SlowestQueue.Should().NotBeEmpty();
        liquidity.SlowestQueue.Select(q => q.Manager).Distinct().Should().HaveCount(liquidity.Lines.First(x => x.Days is not null).Managers);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1.5")]
    public async Task Liquidity_ParticipationOutsideRange_IsRejected(string participation)
    {
        HttpResponseMessage response = await _client.GetAsync($"/api/liquidity/Fund/{FundId}?participation={participation}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UnknownScope_IsNotFound_AndSignInIsRequired()
    {
        (await _client.GetAsync("/api/risk/Fund/999999")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.GetAsync("/api/liquidity/Sleeve/999999")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        HttpClient anonymous = TestDatabase.CreateClient(database.App);
        (await anonymous.GetAsync($"/api/risk/Fund/{FundId}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
