using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using KatalepsisFT.Application.Auth;
using KatalepsisFT.Application.Holdings;
using KatalepsisFT.Domain.Access;
using KatalepsisFT.Domain.Instruments;
using KatalepsisFT.Web.Tests.Demo;
using Microsoft.AspNetCore.Mvc.Testing;

namespace KatalepsisFT.Web.Tests.Funds;

public class HoldingsApiTests(TestDatabase database) : IClassFixture<TestDatabase>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private HttpClient _client = null!;
    private int _fundId;

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
        _fundId = (await Get<List<FundListItemDto>>("/api/funds")).Single().Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<T> Get<T>(string url) => (await _client.GetFromJsonAsync<T>(url, Json))!;

    [Fact]
    public async Task Funds_ListsTheDemoFund_WithItsLatestDay()
    {
        FundListItemDto fund = (await Get<List<FundListItemDto>>("/api/funds")).Single();

        fund.IsDemo.Should().BeTrue();
        fund.AsOf.Should().Be(DemoTestData.LastDay);
        fund.Sleeves.Should().Be(3);
        fund.Managers.Should().Be(9);
        fund.NavUsd.Should().BeGreaterThan(0m);
    }

    [Fact]
    public async Task Overview_SleevesAddUpToTheFund()
    {
        FundOverviewDto overview = await Get<FundOverviewDto>($"/api/funds/{_fundId}/overview");

        overview.Sleeves.Should().HaveCount(3);
        overview.Sleeves.Sum(s => s.Totals.NavUsd).Should().Be(overview.Totals.NavUsd);
        overview.Sleeves.Sum(s => s.Totals.LongUsd).Should().Be(overview.Totals.LongUsd);
        overview.Sleeves.Sum(s => s.Totals.ShortUsd).Should().Be(overview.Totals.ShortUsd);
        overview.Totals.GrossUsd.Should().Be(overview.Totals.LongUsd - overview.Totals.ShortUsd);
        overview.Totals.NetUsd.Should().Be(overview.Totals.LongUsd + overview.Totals.ShortUsd);
        overview.ByType.Select(t => t.Type).Should().Equal(InstrumentType.Spot, InstrumentType.Perp);
        overview.TopAssets.Should().BeInDescendingOrder(a => a.GrossUsd);
        overview.Fund.Benchmark.Should().Be("BTC");
    }

    [Fact]
    public async Task Overview_PerpFilter_KeepsNavAndNarrowsExposure()
    {
        FundOverviewDto all = await Get<FundOverviewDto>($"/api/funds/{_fundId}/overview");
        FundOverviewDto perps = await Get<FundOverviewDto>($"/api/funds/{_fundId}/overview?filter=Perp");

        perps.Totals.NavUsd.Should().Be(all.Totals.NavUsd);
        perps.Totals.LongUsd.Should().BeLessThan(all.Totals.LongUsd);
        perps.Totals.ShortUsd.Should().Be(all.ByType.Single(t => t.Type == InstrumentType.Perp).ShortUsd);
        perps.TopAssets.Should().OnlyContain(a => a.NetByType.Keys.All(k => k == InstrumentType.Perp));
    }

    [Fact]
    public async Task Holdings_CrowdedAsset_ShowsFiveHoldersOnBothSides_AndItsPrice()
    {
        FundHoldingsDto holdings = await Get<FundHoldingsDto>($"/api/funds/{_fundId}/holdings");
        AssetRowDto ondo = holdings.Assets.Single(a => a.Asset.Symbol == "ONDO");

        ondo.Holders.Should().HaveCount(5);
        ondo.HasOffsettingManagers.Should().BeTrue();
        ondo.SleeveIds.Should().HaveCount(2);
        ondo.GrossUsd.Should().Be(ondo.LongUsd - ondo.ShortUsd);
        ondo.Asset.Price.Should().Be(DemoTestDataPrice(DemoTestData.LastDay));
        ondo.Asset.ChangeYtd.Should().BeNegative();
        ondo.Asset.AverageDailyVolumeUsd.Should().Be(2_000_000m);
        holdings.Assets.Should().NotContain(a => a.Asset.Symbol == "USDC");
        holdings.Assets.Sum(a => a.LongUsd).Should().Be(holdings.Totals.LongUsd);
    }

    [Fact]
    public async Task Sleeve_Directional_HasItsManagersLimitsAndEveryPairOfOverlaps()
    {
        FundOverviewDto overview = await Get<FundOverviewDto>($"/api/funds/{_fundId}/overview");
        int sleeveId = overview.Sleeves.Single(s => s.Sleeve.Name == "Directional").Sleeve.Id;

        SleeveDetailDto sleeve = await Get<SleeveDetailDto>($"/api/sleeves/{sleeveId}");

        sleeve.Managers.Should().HaveCount(4);
        sleeve.Managers.Sum(m => m.Totals.NavUsd).Should().Be(sleeve.Totals.NavUsd);
        sleeve.FundNavUsd.Should().Be(overview.Totals.NavUsd);
        sleeve.Limits.Should().Contain(l => l.Kind == Domain.Funds.MandateLimitKind.SingleAssetMax && l.Value == 0.15m);
        sleeve.Overlaps.Should().HaveCount(6, "four managers make six pairs");
        sleeve.Overlaps.Should().Contain(o => o.OppositeSides > 0, "one manager is short the crowded asset");
        sleeve.Assets.Single(a => a.Asset.Symbol == "ONDO").Holders.Should().HaveCount(4);
    }

    [Fact]
    public async Task Manager_PositionsAddUpToItsNav_AndIncludeCash()
    {
        FundOverviewDto overview = await Get<FundOverviewDto>($"/api/funds/{_fundId}/overview");
        SleeveDetailDto sleeve = await Get<SleeveDetailDto>($"/api/sleeves/{overview.Sleeves.Single(s => s.Sleeve.Name == "Directional").Sleeve.Id}");
        int managerId = sleeve.Managers.Single(m => m.Manager.Name == "Ardent L/S").Manager.Id;

        ManagerDetailDto manager = await Get<ManagerDetailDto>($"/api/managers/{managerId}");

        manager.Positions.Sum(p => p.MarketValueUsd).Should().Be(manager.Totals.NavUsd);
        manager.Positions.Last().Type.Should().Be(InstrumentType.Cash);
        manager.Positions.Should().Contain(p => p.Type == InstrumentType.Perp && p.Quantity < 0m);
        manager.SleeveNavUsd.Should().Be(sleeve.Totals.NavUsd);
        manager.Sleeve.Name.Should().Be("Directional");
    }

    [Fact]
    public async Task Asset_LoopBack_ListsEverySleeve_AndMatchesTheHoldingsRow()
    {
        FundHoldingsDto holdings = await Get<FundHoldingsDto>($"/api/funds/{_fundId}/holdings");
        AssetRowDto row = holdings.Assets.Single(a => a.Asset.Symbol == "ONDO");

        AssetDetailDto asset = await Get<AssetDetailDto>($"/api/funds/{_fundId}/assets/ondo");

        asset.GrossUsd.Should().Be(row.GrossUsd);
        asset.NetUsd.Should().Be(row.NetUsd);
        asset.Managers.Should().Be(5);
        asset.Sleeves.Should().HaveCount(3);
        asset.Sleeves.Single(s => s.Sleeve.Name == "Beta").Positions.Should().BeEmpty();
        asset.Sleeves.Sum(s => s.Positions.Count).Should().Be(6, "one manager holds it as spot and as a perp");
        asset.FundNavUsd.Should().Be(holdings.Totals.NavUsd);
    }

    [Theory]
    [InlineData("/api/funds/999999/overview")]
    [InlineData("/api/funds/999999/holdings")]
    [InlineData("/api/sleeves/999999")]
    [InlineData("/api/managers/999999")]
    public async Task UnknownId_Returns404(string url)
    {
        (await _client.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UnknownAsset_Returns404_AndNoSession_Returns401()
    {
        (await _client.GetAsync($"/api/funds/{_fundId}/assets/NOPE")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        HttpClient anonymous = TestDatabase.CreateClient(database.App);
        (await anonymous.GetAsync("/api/funds")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static decimal DemoTestDataPrice(DateOnly day) => FakePrices.PriceOn(day);
}
