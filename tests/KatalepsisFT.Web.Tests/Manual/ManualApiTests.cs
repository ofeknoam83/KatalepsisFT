using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using KatalepsisFT.Application.Auth;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Application.Holdings;
using KatalepsisFT.Application.Manual;
using KatalepsisFT.Application.Structure;
using KatalepsisFT.Domain.Access;
using KatalepsisFT.Domain.Funds;
using KatalepsisFT.Domain.Instruments;
using KatalepsisFT.Persistence;
using KatalepsisFT.Web.Manual;
using KatalepsisFT.Web.Tests.Demo;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KatalepsisFT.Web.Tests.Manual;

/// <summary>
/// The demo data gives BTC and ETH a price feed: on the fake prices every asset's latest close is 79.0. AAPL has none.
/// </summary>
public class ManualApiTests(TestDatabase database) : IClassFixture<TestDatabase>, IAsyncLifetime
{
    private const decimal FeedPrice = 79.0m;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        WebApplicationFactory<Program> app = database.App;
        if (!await database.IsSeededAsync(app))
        {
            await DemoTestData.SeedAsync(app);
        }

        _admin = await SignInAsync(Roles.Admin);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<HttpClient> SignInAsync(string role)
    {
        string email = await database.AddUserAsync(database.App, role);
        HttpClient client = TestDatabase.CreateClient(database.App);
        await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, TestDatabase.Password, false));
        return client;
    }

    private async Task<T> Get<T>(string url) => (await _admin.GetFromJsonAsync<T>(url, Json))!;

    private async Task<(int FundId, int ManagerId)> NewFundAsync()
    {
        NewFundRequest request = new($"Fund {Guid.NewGuid():N}", null, [new NewSleeveRequest("TradFi", MandateType.Other, [new NewManagerRequest("Equity Desk", 0m)])]);
        int fundId = (await (await _admin.PostAsJsonAsync("/api/funds", request, Json)).Content.ReadFromJsonAsync<FundCreatedDto>(Json))!.Id;
        return (fundId, (await Get<StructureDto>($"/api/funds/{fundId}/structure")).Sleeves[0].Managers[0].Id);
    }

    private static ManualPositionRequest Position(InstrumentType type, string asset, decimal quantity, decimal? price = null, decimal? entry = null) => new(type, asset, quantity, price, entry, null, null, null, null, null, null);

    private Task<HttpResponseMessage> AddAsync(int managerId, ManualPositionRequest request) => _admin.PostAsJsonAsync($"/api/managers/{managerId}/manual", request, Json);

    private async Task<IReadOnlyList<string>> ErrorsAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        return (await response.Content.ReadFromJsonAsync<ManualErrorsResponse>(Json))!.Errors;
    }

    [Fact]
    public async Task EnteredPositions_ShowOnTheFund_ValuedAtTheirOwnPriceOrTheFeed()
    {
        (int fundId, int managerId) = await NewFundAsync();

        (await AddAsync(managerId, Position(InstrumentType.Equity, "aapl", 100m, 250m, 200m))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await AddAsync(managerId, Position(InstrumentType.Spot, "BTC", 2m))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await AddAsync(managerId, Position(InstrumentType.Perp, "ETH", -10m, 3_000m, 3_100m))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await AddAsync(managerId, Position(InstrumentType.Cash, "USD", 5_000m))).StatusCode.Should().Be(HttpStatusCode.NoContent);

        ManualBookDto book = await Get<ManualBookDto>($"/api/managers/{managerId}/manual");
        TotalsDto totals = (await Get<FundOverviewDto>($"/api/funds/{fundId}/overview")).Totals;
        ManagerDetailDto manager = await Get<ManagerDetailDto>($"/api/managers/{managerId}");

        book.CanEdit.Should().BeTrue();
        book.Positions.Should().HaveCount(4);
        book.Positions.Single(p => p.Asset == "AAPL").Should().Match<ManualPositionDto>(p => p.MarkPrice == 250m && !p.PriceFromFeed && p.UpdatedBy != null);
        book.Positions.Single(p => p.Asset == "BTC").Should().Match<ManualPositionDto>(p => p.MarkPrice == FeedPrice && p.PriceFromFeed && p.Price == null);
        // 25,000 of AAPL + 158 of BTC + 5,000 cash + 1,000 profit on the short perp.
        totals.NavUsd.Should().Be(25_000m + 2m * FeedPrice + 5_000m + 1_000m);
        totals.LongUsd.Should().Be(25_000m + 2m * FeedPrice);
        totals.ShortUsd.Should().Be(-30_000m);
        totals.CashUsd.Should().Be(5_000m);
        manager.Positions.Should().HaveCount(4);
        manager.Positions.Should().Contain(p => p.Type == InstrumentType.Equity && p.Asset == "AAPL" && p.MarketValueUsd == 25_000m);

        using IServiceScope scope = database.App.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<FtDbContext>().Assets.Where(a => a.Symbol == "AAPL").Select(a => a.AssetClass).SingleAsync()).Should().Be(AssetClass.Equity);
    }

    [Fact]
    public async Task ChangingAndRemovingAPosition_ChangesTheBookAtOnce()
    {
        (int fundId, int managerId) = await NewFundAsync();
        await AddAsync(managerId, Position(InstrumentType.Equity, "MSFT", 10m, 400m));
        await AddAsync(managerId, Position(InstrumentType.Cash, "USD", 1_000m));
        ManualPositionDto msft = (await Get<ManualBookDto>($"/api/managers/{managerId}/manual")).Positions.Single(p => p.Asset == "MSFT");

        (await _admin.PutAsJsonAsync($"/api/manual-positions/{msft.Id}", Position(InstrumentType.Equity, "MSFT", 20m, 410m), Json)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        decimal afterChange = (await Get<FundOverviewDto>($"/api/funds/{fundId}/overview")).Totals.NavUsd;
        (await _admin.DeleteAsync($"/api/manual-positions/{msft.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        decimal afterRemoval = (await Get<FundOverviewDto>($"/api/funds/{fundId}/overview")).Totals.NavUsd;

        afterChange.Should().Be(9_200m);
        afterRemoval.Should().Be(1_000m);
        (await _admin.DeleteAsync($"/api/manual-positions/{msft.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PositionThatCannotBeValued_IsRefused_WithTheReason()
    {
        (_, int managerId) = await NewFundAsync();

        (await ErrorsAsync(await AddAsync(managerId, Position(InstrumentType.Equity, "AAPL", 10m)))).Single().Should().Be("AAPL has no price feed, so the position needs a price.");
        (await ErrorsAsync(await AddAsync(managerId, Position(InstrumentType.Spot, "BTC", 0m)))).Single().Should().Contain("cannot be zero");
        (await ErrorsAsync(await AddAsync(managerId, Position(InstrumentType.Spot, "not a ticker!", 1m, 1m)))).Single().Should().Contain("ticker");
        (await ErrorsAsync(await AddAsync(managerId, Position(InstrumentType.Spot, "BTC", 1m, -5m)))).Single().Should().Contain("above zero");
        (await ErrorsAsync(await AddAsync(managerId, Position(InstrumentType.Future, "BTC", 1m)))).Single().Should().Be("a future needs its expiry.");
        (await ErrorsAsync(await AddAsync(managerId, Position(InstrumentType.Option, "BTC", 1m, 5m)))).Single().Should().StartWith("an option needs");
        (await Get<ManualBookDto>($"/api/managers/{managerId}/manual")).Positions.Should().BeEmpty();
    }

    [Fact]
    public async Task Option_IsValuedAtItsPremium_WithDeltaAdjustedExposure()
    {
        (int fundId, int managerId) = await NewFundAsync();
        ManualPositionRequest option = new(InstrumentType.Option, "BTC", -2m, 3_200m, 4_000m, new DateOnly(2026, 12, 25), 90_000m, OptionRight.Call, 0.35m, 80_000m, "Sold over the counter");

        (await AddAsync(managerId, option)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        TotalsDto totals = (await Get<FundOverviewDto>($"/api/funds/{fundId}/overview")).Totals;

        // A short option is a liability of its premium; its exposure is delta × quantity × underlying.
        totals.NavUsd.Should().Be(-6_400m);
        totals.ShortUsd.Should().Be(-56_000m);
        (await ErrorsAsync(await AddAsync(managerId, option with { Delta = 1.5m }))).Single().Should().Contain("between −1 and 1");
        (await ErrorsAsync(await AddAsync(managerId, option with { Asset = "NOFEED", UnderlyingPrice = null }))).Single().Should().Contain("price of its underlying");
    }

    [Fact]
    public async Task Upload_ReplacesTheWholeBook()
    {
        (int fundId, int managerId) = await NewFundAsync();
        await AddAsync(managerId, Position(InstrumentType.Equity, "OLD", 1m, 10m));
        string csv = """
            type,asset,quantity,price,entry_price,expiry,strike,right,delta,underlying_price,note
            Equity,AAPL,100,250,200,,,,,,"Core holding, long term"
            equity,TSLA,-50,300,,,,,,,
            Spot,BTC,2,,,,,,,,

            Future,BTC,1,81000,80000,2026-12-25,,,,,
            Option,ETH,10,150,120,2026-12-25,4000,put,-0.3,3000,
            Cash,USD,5000,,,,,,,,
            """;

        HttpResponseMessage response = await _admin.PostAsJsonAsync($"/api/managers/{managerId}/manual/upload", new UploadRequest(csv), Json);
        ManualBookDto book = await Get<ManualBookDto>($"/api/managers/{managerId}/manual");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
        book.Positions.Select(p => p.Asset).Should().BeEquivalentTo("AAPL", "TSLA", "BTC", "BTC", "ETH", "USD");
        book.Positions.Should().NotContain(p => p.Asset == "OLD");
        book.Positions.Single(p => p.Asset == "AAPL").Note.Should().Be("Core holding, long term");
        book.Positions.Single(p => p.Type == InstrumentType.Option).Should().Match<ManualPositionDto>(p => p.OptionRight == OptionRight.Put && p.Strike == 4_000m && p.Delta == -0.3m && p.Expiry == new DateOnly(2026, 12, 25));
        // 25,000 − 15,000 + 158 + 1,000 future profit + 1,500 option premium + 5,000 cash.
        (await Get<FundOverviewDto>($"/api/funds/{fundId}/overview")).Totals.NavUsd.Should().Be(25_000m - 15_000m + 2m * FeedPrice + 1_000m + 1_500m + 5_000m);
    }

    [Fact]
    public async Task Upload_WithOneBadLine_ChangesNothing_AndNamesEveryBadLine()
    {
        (int fundId, int managerId) = await NewFundAsync();
        await AddAsync(managerId, Position(InstrumentType.Equity, "KEEP", 1m, 10m));
        string csv = "type;asset;quantity;price\nEquity;AAPL;100;250\nBond;XYZ;5;1\nEquity;MSFT;abc;400\nEquity;NOPRICE;5;\n";

        IReadOnlyList<string> errors = await ErrorsAsync(await _admin.PostAsJsonAsync($"/api/managers/{managerId}/manual/upload", new UploadRequest(csv), Json));

        errors.Should().HaveCount(2);
        errors[0].Should().StartWith("Line 3:").And.Contain("\"Bond\" is not a type");
        errors[1].Should().StartWith("Line 4:").And.Contain("\"abc\" in quantity is not a number");
        (await Get<ManualBookDto>($"/api/managers/{managerId}/manual")).Positions.Select(p => p.Asset).Should().Equal("KEEP");
        (await Get<FundOverviewDto>($"/api/funds/{fundId}/overview")).Totals.NavUsd.Should().Be(10m);

        // Lines that read correctly but cannot be valued are caught in a second pass, again without changing anything.
        IReadOnlyList<string> second = await ErrorsAsync(await _admin.PostAsJsonAsync($"/api/managers/{managerId}/manual/upload", new UploadRequest("type,asset,quantity,price\nEquity,AAPL,100,250\nEquity,NOPRICE,5,\n"), Json));
        second.Single().Should().Be("Line 3: NOPRICE has no price feed, so the position needs a price.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("asset,quantity\nAAPL,1\n")]
    [InlineData("type,asset,quantity,price\n")]
    public async Task Upload_OfAnEmptyOrHeaderlessFile_IsRefused(string csv)
    {
        (_, int managerId) = await NewFundAsync();

        HttpResponseMessage response = await _admin.PostAsJsonAsync($"/api/managers/{managerId}/manual/upload", new UploadRequest(csv), Json);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task CashFlows_CanBeEnteredAndRemoved_ButNotOnesReadFromAVenue()
    {
        (_, int managerId) = await NewFundAsync();
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        (await _admin.PostAsJsonAsync($"/api/managers/{managerId}/cash-flows", new CashFlowRequest(today, 1_000_000m, "Initial subscription"), Json)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _admin.PostAsJsonAsync($"/api/managers/{managerId}/cash-flows", new CashFlowRequest(today.AddDays(-1), -250_000m, null), Json)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ErrorsAsync(await _admin.PostAsJsonAsync($"/api/managers/{managerId}/cash-flows", new CashFlowRequest(today, 0m, null), Json))).Should().ContainSingle();
        (await ErrorsAsync(await _admin.PostAsJsonAsync($"/api/managers/{managerId}/cash-flows", new CashFlowRequest(today.AddDays(1), 5m, null), Json))).Single().Should().Contain("today or earlier");
        long venueFlow;
        using (IServiceScope scope = database.App.Services.CreateScope())
        {
            FtDbContext db = scope.ServiceProvider.GetRequiredService<FtDbContext>();
            Domain.Positions.CashFlow flow = new() { ManagerAccountId = managerId, Date = today, AmountUsd = 7m, Kind = Domain.Positions.CashFlowKind.Subscription, ExternalId = "Deribit:1:dep:1" };
            db.CashFlows.Add(flow);
            await db.SaveChangesAsync();
            venueFlow = flow.Id;
        }

        ManualBookDto book = await Get<ManualBookDto>($"/api/managers/{managerId}/manual");
        CashFlowDto manualFlow = book.CashFlows.Single(f => f.AmountUsd == 1_000_000m);

        book.CashFlows.Select(f => f.AmountUsd).Should().BeEquivalentTo([1_000_000m, -250_000m, 7m]);
        manualFlow.Should().Match<CashFlowDto>(f => !f.FromVenue && f.Note == "Initial subscription");
        (await _admin.DeleteAsync($"/api/cash-flows/{manualFlow.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ErrorsAsync(await _admin.DeleteAsync($"/api/cash-flows/{venueFlow}"))).Single().Should().Contain("read from a venue");
    }

    [Fact]
    public async Task ManualOnlyBooks_AreRevaluedOnATimer()
    {
        (int fundId, int managerId) = await NewFundAsync();
        await AddAsync(managerId, Position(InstrumentType.Spot, "BTC", 2m));
        using IServiceScope scope = database.App.Services.CreateScope();
        FtDbContext db = scope.ServiceProvider.GetRequiredService<FtDbContext>();
        // Tomorrow's close has arrived.
        int btc = await db.Assets.Where(a => a.Symbol == "BTC").Select(a => a.Id).SingleAsync();
        DateOnly next = (await db.PriceBars.Where(b => b.AssetId == btc).MaxAsync(b => b.Date)).AddDays(1);
        db.PriceBars.Add(new PriceBar { AssetId = btc, Date = next, Close = 100m, VolumeUsd = 1m, Source = "test" });
        await db.SaveChangesAsync();

        try
        {
            int refreshed = await scope.ServiceProvider.GetRequiredService<ManagerBookService>().RefreshManualOnlyAsync(CancellationToken.None);

            refreshed.Should().BeGreaterThanOrEqualTo(1);
            (await Get<FundOverviewDto>($"/api/funds/{fundId}/overview")).Totals.NavUsd.Should().Be(200m);
        }
        finally
        {
            await db.PriceBars.Where(b => b.AssetId == btc && b.Date == next).ExecuteDeleteAsync();
        }
    }

    /// <summary>A share price service that knows one ticker.</summary>
    private sealed class FakeShares : KatalepsisFT.Application.Prices.IDailyPriceSource
    {
        public List<string> Asked { get; } = [];

        public string Name => "fake-shares";

        public AssetClass AssetClass => AssetClass.Equity;

        public Task<IReadOnlyList<KatalepsisFT.Application.Prices.DailyBar>?> GetDailyBarsAsync(string assetSymbol, DateOnly from, DateOnly to, CancellationToken cancellationToken)
        {
            Asked.Add(assetSymbol);
            IReadOnlyList<KatalepsisFT.Application.Prices.DailyBar>? bars = assetSymbol == "NVDA" ? [new(to.AddDays(-1), 880m, 1_000_000m), new(to, 900m, 2_000_000m)] : null;
            return Task.FromResult(bars);
        }
    }

    [Fact]
    public async Task ShareEnteredWithoutAPrice_GetsItsPriceFromTheFeed_OnTheSpot()
    {
        FakeShares shares = new();
        WebApplicationFactory<Program> app = database.CreateApp(services: s => s.AddSingleton<KatalepsisFT.Application.Prices.IDailyPriceSource>(shares));
        string email = await database.AddUserAsync(app, Roles.Admin);
        HttpClient admin = TestDatabase.CreateClient(app);
        await admin.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, TestDatabase.Password, false));
        (int fundId, int managerId) = await NewFundAsync();

        HttpResponseMessage known = await admin.PostAsJsonAsync($"/api/managers/{managerId}/manual", Position(InstrumentType.Equity, "nvda", 10m), Json);
        HttpResponseMessage unknown = await admin.PostAsJsonAsync($"/api/managers/{managerId}/manual", Position(InstrumentType.Equity, "ZZZZ", 10m), Json);
        HttpResponseMessage again = await admin.PostAsJsonAsync($"/api/managers/{managerId}/manual", Position(InstrumentType.Equity, "NVDA", 5m), Json);
        ManualBookDto book = await Get<ManualBookDto>($"/api/managers/{managerId}/manual");

        known.StatusCode.Should().Be(HttpStatusCode.NoContent, await known.Content.ReadAsStringAsync());
        (await ErrorsAsync(unknown)).Single().Should().Be("ZZZZ has no price feed, so the position needs a price.");
        again.StatusCode.Should().Be(HttpStatusCode.NoContent);
        book.Positions.Should().HaveCount(2).And.OnlyContain(p => p.Asset == "NVDA" && p.MarkPrice == 900m && p.PriceFromFeed);
        (await Get<FundOverviewDto>($"/api/funds/{fundId}/overview")).Totals.NavUsd.Should().Be(13_500m);
        // Once its history is stored, the service is not asked about NVDA again.
        shares.Asked.Count(a => a == "NVDA").Should().Be(1);
    }

    [Fact]
    public async Task ExampleFund_AndViewers_CannotEnterPositions()
    {
        (_, int managerId) = await NewFundAsync();
        int demoFund = (await Get<List<FundListItemDto>>("/api/funds")).Single(f => f.IsDemo).Id;
        int demoManager = (await Get<StructureDto>($"/api/funds/{demoFund}/structure")).Sleeves[0].Managers[0].Id;
        HttpClient viewer = await SignInAsync(Roles.Viewer);

        (await Get<ManualBookDto>($"/api/managers/{demoManager}/manual")).CanEdit.Should().BeFalse();
        (await ErrorsAsync(await AddAsync(demoManager, Position(InstrumentType.Cash, "USD", 1m)))).Single().Should().Contain("example fund");
        (await viewer.GetAsync($"/api/managers/{managerId}/manual")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.PostAsJsonAsync($"/api/managers/{managerId}/manual", Position(InstrumentType.Cash, "USD", 1m), Json)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.PostAsJsonAsync($"/api/managers/{managerId}/manual/upload", new UploadRequest("x"), Json)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _admin.GetAsync("/api/managers/999999/manual")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await AddAsync(999_999, Position(InstrumentType.Cash, "USD", 1m))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
