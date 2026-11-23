using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using KatalepsisFT.Application.Auth;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Application.Execution;
using KatalepsisFT.Application.Structure;
using KatalepsisFT.Domain.Access;
using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Domain.Execution;
using KatalepsisFT.Domain.Funds;
using KatalepsisFT.Domain.Instruments;
using KatalepsisFT.Infrastructure.Execution;
using KatalepsisFT.Persistence;
using KatalepsisFT.Web.Tests.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KatalepsisFT.Web.Tests.Execution;

/// <summary>
/// The order path end to end, on paper: an order is written down by the web service, the engine checks and fills it
/// against a book the test controls, and everything is read back through the same endpoints the screens use.
/// </summary>
public class ExecutionApiTests(TestDatabase database) : IClassFixture<TestDatabase>, IAsyncLifetime
{
    private const string ExecMasterKey = "an-execution-master-key-that-is-long-enough-123";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly ConsolidatedBook Books = new(TimeProvider.System);
    private static readonly FakeVenue Binance = new(Venue.Binance);

    // One app for the whole class: every test makes its own fund, so they do not get in each other's way.
    private static WebApplicationFactory<Program>? _app;
    private HttpClient _admin = null!;

    private sealed record Answer<T>(T? Value, string? Notice);

    public async Task InitializeAsync()
    {
        _app ??= database.CreateApp(services: services =>
        {
            foreach (ServiceDescriptor descriptor in services.Where(s => s.ServiceType == typeof(IBookFeed) || s.ServiceType == typeof(IExecutionSignal) || s.ServiceType == typeof(IVenueAdapter)).ToList())
            {
                services.Remove(descriptor);
            }

            // The engine and the web service share one book here; on Render the book travels between them.
            services.AddSingleton(Books);
            services.AddSingleton<IBookFeed, LocalBookFeed>();
            services.AddSingleton<IExecutionSignal, NoExecutionSignal>();
            services.AddSingleton<IVenueAdapter>(Binance);
            services.AddScoped<ExecutionEngine>();
        });
        _admin = await SignInAsync(Roles.Admin);
        Quote(Venue.Binance, "BTC", 59_990m, 60_010m);
        Quote(Venue.Bybit, "BTC", 59_995m, 60_005m);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<HttpClient> SignInAsync(string role)
    {
        string email = await database.AddUserAsync(_app!, role);
        HttpClient client = TestDatabase.CreateClient(_app!);
        await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, TestDatabase.Password, false));
        return client;
    }

    /// <summary>A venue's book: ten BTC a level, three levels a side, one dollar apart beyond the best.</summary>
    private static void Quote(Venue venue, string asset, decimal bid, decimal ask, decimal size = 10m) =>
        Books.Update(new VenueBook(venue, asset, InstrumentType.Perp, [new(bid, size), new(bid - 1m, size), new(bid - 2m, size)], [new(ask, size), new(ask + 1m, size), new(ask + 2m, size)], DateTimeOffset.UtcNow));

    private async Task RunEngineAsync()
    {
        using IServiceScope scope = _app!.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ExecutionEngine>().RunOnceAsync(CancellationToken.None);
    }

    /// <summary>A new fund with one manager and a paper account for it on a venue; returns the fund, the manager and the account.</summary>
    private async Task<(int FundId, int ManagerId, int AccountId)> NewAccountAsync(Venue venue = Venue.Binance, decimal maxOrder = 1_000_000m, decimal maxPosition = 2_000_000m)
    {
        NewFundRequest request = new($"SMA {Guid.NewGuid():N}", null, [new NewSleeveRequest("Directional", MandateType.Directional, [new NewManagerRequest("Alpha Desk", 1_000_000m)])], FundKind.Sma, "A Client");
        HttpResponseMessage created = await _admin.PostAsJsonAsync("/api/funds", request, Json);
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        int fundId = (await created.Content.ReadFromJsonAsync<FundCreatedDto>(Json))!.Id;
        int managerId = (await _admin.GetFromJsonAsync<StructureDto>($"/api/funds/{fundId}/structure", Json))!.Sleeves[0].Managers[0].Id;
        return (fundId, managerId, await AddAccountAsync(managerId, venue, maxOrder, maxPosition));
    }

    private async Task<int> AddAccountAsync(int managerId, Venue venue, decimal maxOrder = 1_000_000m, decimal maxPosition = 2_000_000m)
    {
        HttpResponseMessage added = await _admin.PostAsJsonAsync("/api/trading-accounts", new SaveTradingAccountRequest(managerId, venue, "Main", 3m, MarginMode.Cross, maxOrder, maxPosition, 100, 1_000_000m), Json);
        added.StatusCode.Should().Be(HttpStatusCode.OK, await added.Content.ReadAsStringAsync());
        return (await added.Content.ReadFromJsonAsync<Answer<int>>(Json))!.Value;
    }

    private static SubmitOrderRequest Buy(int accountId, decimal quantity, decimal? limit = null, TimeInForce tif = TimeInForce.GoodTillCancelled, bool reduceOnly = false, string? name = null) =>
        new(accountId, null, "btc", InstrumentType.Perp, OrderSide.Buy, limit is null ? OrderType.Market : OrderType.Limit, tif, quantity, limit, reduceOnly, name);

    private static SubmitOrderRequest Sell(int accountId, decimal quantity, decimal? limit = null, TimeInForce tif = TimeInForce.GoodTillCancelled, bool reduceOnly = false) =>
        Buy(accountId, quantity, limit, tif, reduceOnly) with { Side = OrderSide.Sell };

    /// <summary>Sends an order, lets the engine look at it once, and reads it back.</summary>
    private async Task<OrderDto> TradeAsync(SubmitOrderRequest request, HttpClient? client = null)
    {
        HttpResponseMessage sent = await (client ?? _admin).PostAsJsonAsync("/api/orders", request, Json);
        sent.StatusCode.Should().Be(HttpStatusCode.OK, await sent.Content.ReadAsStringAsync());
        long id = (await sent.Content.ReadFromJsonAsync<Answer<OrderDto>>(Json))!.Value!.Id;
        await RunEngineAsync();
        return (await _admin.GetFromJsonAsync<OrderDto>($"/api/orders/{id}", Json))!;
    }

    [Fact]
    public async Task MarketOrder_FillsAgainstTheBookItIsShown_AndThePositionAndFeesFollow()
    {
        (int fundId, _, int accountId) = await NewAccountAsync();

        // 15 BTC against 10 at 60,010 and 10 at 60,011.
        OrderDto order = await TradeAsync(Buy(accountId, 15m));

        order.Status.Should().Be(OrderStatus.Filled);
        order.FilledQuantity.Should().Be(15m);
        order.AverageFillPrice.Should().BeApproximately((10m * 60_010m + 5m * 60_011m) / 15m, 0.0001m);
        order.ArrivalPrice.Should().Be(60_000m);
        order.SlippageBps.Should().BeApproximately((order.AverageFillPrice - 60_000m) / 60_000m * 10_000m, 0.01m);
        order.FeesUsd.Should().BeApproximately(15m * order.AverageFillPrice * 0.0005m, 0.01m);
        order.Events.Select(e => e.Kind).Should().Equal(OrderEventKind.Received, OrderEventKind.Accepted, OrderEventKind.Fill, OrderEventKind.Fill);
        order.Events.Where(e => e.Kind == OrderEventKind.Fill).Should().OnlyContain(e => e.Venue == Venue.Binance);
        TradingAccountDto account = (await _admin.GetFromJsonAsync<TradingDto>($"/api/funds/{fundId}/trading", Json))!.Accounts.Single();
        TradingPositionDto position = account.Positions.Single();
        position.Quantity.Should().Be(15m);
        position.AveragePrice.Should().BeApproximately(order.AverageFillPrice, 0.0001m);
        position.MarkPrice.Should().Be(60_000m);
        account.EquityUsd.Should().BeApproximately(1_000_000m - order.FeesUsd + 15m * (60_000m - order.AverageFillPrice), 0.02m);
    }

    [Fact]
    public async Task LimitOrder_RestsUntilTheMarketComesToIt_ThenFillsAtItsOwnPriceAsTheMaker()
    {
        (_, _, int accountId) = await NewAccountAsync();

        OrderDto resting = await TradeAsync(Buy(accountId, 4m, 59_980m));
        // Only three are offered at or below the limit; the next offer is above it.
        Books.Update(new VenueBook(Venue.Binance, "BTC", InstrumentType.Perp, [new(59_960m, 3m)], [new(59_975m, 3m), new(59_990m, 3m)], DateTimeOffset.UtcNow));
        await RunEngineAsync();
        OrderDto partly = (await _admin.GetFromJsonAsync<OrderDto>($"/api/orders/{resting.Id}", Json))!;
        await RunEngineAsync();
        OrderDto filled = (await _admin.GetFromJsonAsync<OrderDto>($"/api/orders/{resting.Id}", Json))!;
        Quote(Venue.Binance, "BTC", 59_990m, 60_010m);

        resting.Status.Should().Be(OrderStatus.Working);
        // Three were offered at or below the limit; the fourth fills on the next look.
        partly.Status.Should().Be(OrderStatus.PartlyFilled);
        partly.FilledQuantity.Should().Be(3m);
        filled.Status.Should().Be(OrderStatus.Filled);
        filled.AverageFillPrice.Should().Be(59_980m);
        filled.FeesUsd.Should().BeApproximately(4m * 59_980m * 0.0002m, 0.001m);
    }

    [Theory]
    [InlineData("too large", "this account's largest order")]
    [InlineData("position", "this account's largest position")]
    [InlineData("far price", "through the market")]
    [InlineData("no market", "There is no live market")]
    [InlineData("post-only crossing", "must not trade at once")]
    [InlineData("reduce-only without a position", "needs a position on the other side")]
    public async Task OrderThatBreaksARule_IsRejected_WithTheReason_AndNothingTrades(string what, string expected)
    {
        (int fundId, _, int accountId) = await NewAccountAsync(maxOrder: 300_000m, maxPosition: 500_000m);
        if (what == "position")
        {
            (await TradeAsync(Buy(accountId, 4.9m))).Status.Should().Be(OrderStatus.Filled);
            (await TradeAsync(Buy(accountId, 3m))).Status.Should().Be(OrderStatus.Filled);
        }

        SubmitOrderRequest request = what switch
        {
            "too large" => Buy(accountId, 6m),
            "position" => Buy(accountId, 1m),
            "far price" => Buy(accountId, 1m, 61_000m),
            "no market" => Buy(accountId, 1m) with { Asset = "NOSUCH" },
            "post-only crossing" => Buy(accountId, 1m, 60_010m, TimeInForce.PostOnly),
            _ => Sell(accountId, 1m, reduceOnly: true),
        };

        OrderDto order = await TradeAsync(request);

        order.Status.Should().Be(OrderStatus.Rejected);
        order.RejectReason.Should().Contain(expected);
        order.FilledQuantity.Should().Be(0m);
        if (what != "position")
        {
            (await _admin.GetFromJsonAsync<TradingDto>($"/api/funds/{fundId}/trading", Json))!.Accounts.Single().Positions.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task ReduceOnly_ClosesButNeverFlips_AndRealisesTheProfit()
    {
        (int fundId, _, int accountId) = await NewAccountAsync();
        await TradeAsync(Buy(accountId, 2m));
        Quote(Venue.Binance, "BTC", 61_000m, 61_010m);

        OrderDto tooMuch = await TradeAsync(Sell(accountId, 3m, reduceOnly: true));
        OrderDto close = await TradeAsync(Sell(accountId, 2m, reduceOnly: true));
        Quote(Venue.Binance, "BTC", 59_990m, 60_010m);

        tooMuch.Status.Should().Be(OrderStatus.Rejected);
        tooMuch.RejectReason.Should().Contain("may not exceed the position");
        close.Status.Should().Be(OrderStatus.Filled);
        TradingPositionDto flat = (await _admin.GetFromJsonAsync<TradingDto>($"/api/funds/{fundId}/trading", Json))!.Accounts.Single().Positions.Single();
        flat.Quantity.Should().Be(0m);
        flat.RealisedPnlUsd.Should().Be(2m * (61_000m - 60_010m));
    }

    [Fact]
    public async Task SameOrderNameTwice_IsOneOrder()
    {
        (_, _, int accountId) = await NewAccountAsync();

        OrderDto first = await TradeAsync(Buy(accountId, 1m, name: "retry-me"));
        OrderDto again = await TradeAsync(Buy(accountId, 1m, name: "retry-me"));

        again.Id.Should().Be(first.Id);
        again.FilledQuantity.Should().Be(1m);
    }

    [Fact]
    public async Task KillSwitch_CancelsWhatIsOpen_BlocksNewOrders_AndTakesTwoPeopleToRelease()
    {
        (int fundId, _, int accountId) = await NewAccountAsync();
        HttpClient trader = await SignInAsync(Roles.Trader);
        HttpClient secondAdmin = await SignInAsync(Roles.Admin);
        OrderDto resting = await TradeAsync(Buy(accountId, 1m, 59_900m), trader);

        (await trader.PostAsJsonAsync($"/api/funds/{fundId}/halts", new PullHaltRequest(HaltScope.Account, null, accountId, "Something looks wrong"), Json)).StatusCode.Should().Be(HttpStatusCode.OK);
        await RunEngineAsync();
        OrderDto cancelled = (await _admin.GetFromJsonAsync<OrderDto>($"/api/orders/{resting.Id}", Json))!;
        OrderDto blocked = await TradeAsync(Buy(accountId, 1m), trader);
        TradingDto halted = (await _admin.GetFromJsonAsync<TradingDto>($"/api/funds/{fundId}/trading", Json))!;

        // The trader asks for the release; neither the trader nor a viewer may approve it, another administrator may.
        int haltId = halted.Halts.Single().Id;
        (await trader.PostAsync($"/api/halts/{haltId}/release", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        int requestId = (await _admin.GetFromJsonAsync<TradingDto>($"/api/funds/{fundId}/trading", Json))!.Approvals.Single().Id;
        (await trader.PostAsJsonAsync($"/api/approvals/{requestId}/approve", new DecisionRequest(null), Json)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await secondAdmin.PostAsJsonAsync($"/api/approvals/{requestId}/approve", new DecisionRequest("Checked with the desk"), Json)).StatusCode.Should().Be(HttpStatusCode.OK);
        OrderDto after = await TradeAsync(Buy(accountId, 1m), trader);

        cancelled.Status.Should().Be(OrderStatus.Cancelled);
        cancelled.Events[^1].Detail.Should().Be("Cancelled by the kill switch.");
        blocked.Status.Should().Be(OrderStatus.Rejected);
        blocked.RejectReason.Should().Contain("halted");
        halted.Accounts.Single().IsHalted.Should().BeTrue();
        after.Status.Should().Be(OrderStatus.Filled);
        (await _admin.GetFromJsonAsync<TradingDto>($"/api/funds/{fundId}/trading", Json))!.Halts.Should().BeEmpty();
    }

    [Fact]
    public async Task RaisingALimit_NeedsASecondPerson_LoweringOneDoesNot_AndNobodyApprovesTheirOwnRequest()
    {
        (int fundId, int managerId, int accountId) = await NewAccountAsync();
        HttpClient secondAdmin = await SignInAsync(Roles.Admin);

        // Largest order down, largest position up.
        HttpResponseMessage saved = await _admin.PutAsJsonAsync($"/api/trading-accounts/{accountId}", new SaveTradingAccountRequest(managerId, Venue.Binance, "Main", 3m, MarginMode.Cross, 500_000m, 5_000_000m, 100, 1_000_000m), Json);
        TradingDto waiting = (await _admin.GetFromJsonAsync<TradingDto>($"/api/funds/{fundId}/trading", Json))!;
        ApprovalDto request = waiting.Approvals.Single();
        HttpResponseMessage own = await _admin.PostAsJsonAsync($"/api/approvals/{request.Id}/approve", new DecisionRequest(null), Json);
        (await secondAdmin.PostAsJsonAsync($"/api/approvals/{request.Id}/approve", new DecisionRequest(null), Json)).StatusCode.Should().Be(HttpStatusCode.OK);
        TradingAccountDto raised = (await _admin.GetFromJsonAsync<TradingDto>($"/api/funds/{fundId}/trading", Json))!.Accounts.Single();

        saved.StatusCode.Should().Be(HttpStatusCode.OK);
        waiting.Accounts.Single().MaxOrderNotionalUsd.Should().Be(500_000m);
        waiting.Accounts.Single().MaxPositionNotionalUsd.Should().Be(2_000_000m);
        request.Kind.Should().Be(ApprovalKind.RaiseLimits);
        request.CanDecide.Should().BeFalse();
        own.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await own.Content.ReadFromJsonAsync<ErrorResponse>(Json))!.Message.Should().Contain("someone other than the person who made it");
        raised.MaxPositionNotionalUsd.Should().Be(5_000_000m);
    }

    [Theory]
    [InlineData(true, false, null)]
    [InlineData(true, true, "This key can withdraw")]
    [InlineData(false, false, "This key cannot trade")]
    public async Task TradingKey_MustTradeAndMustNotWithdraw_IsSealedSoOnlyTheExecutionServiceCanOpenIt_AndWaitsForApproval(bool canTrade, bool canWithdraw, string? refusal)
    {
        (int fundId, _, int accountId) = await NewAccountAsync();
        HttpClient secondAdmin = await SignInAsync(Roles.Admin);
        SealedTradingVault.TradingKeyOpener opener;
        using (IServiceScope scope = _app!.Services.CreateScope())
        {
            opener = (await SealedTradingVault.EnsureKeysAsync(scope.ServiceProvider.GetRequiredService<FtDbContext>(), ExecMasterKey, CancellationToken.None))!;
        }

        Binance.Check = new CredentialCheck(canTrade, canWithdraw);

        HttpResponseMessage asked = await _admin.PostAsJsonAsync($"/api/trading-accounts/{accountId}/key", new TradingKeyRequest("trading-key-1234", "the-trading-secret", null), Json);

        if (refusal is not null)
        {
            asked.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await asked.Content.ReadFromJsonAsync<ErrorResponse>(Json))!.Message.Should().Contain(refusal);
            return;
        }

        asked.StatusCode.Should().Be(HttpStatusCode.OK, await asked.Content.ReadAsStringAsync());
        TradingDto before = (await _admin.GetFromJsonAsync<TradingDto>($"/api/funds/{fundId}/trading", Json))!;
        (await secondAdmin.PostAsJsonAsync($"/api/approvals/{before.Approvals.Single().Id}/approve", new DecisionRequest(null), Json)).StatusCode.Should().Be(HttpStatusCode.OK);
        TradingAccountDto account = (await _admin.GetFromJsonAsync<TradingDto>($"/api/funds/{fundId}/trading", Json))!.Accounts.Single();
        using IServiceScope read = _app!.Services.CreateScope();
        FtDbContext db = read.ServiceProvider.GetRequiredService<FtDbContext>();
        string stored = (await db.TradingAccounts.Where(a => a.Id == accountId).Select(a => a.EncryptedKey).SingleAsync())!;

        before.Accounts.Single().HasKey.Should().BeFalse();
        account.HasKey.Should().BeTrue();
        account.KeyHint.Should().Be("1234");
        // Still on paper: a key alone does not make an account trade real money.
        account.Mode.Should().Be(TradingMode.Paper);
        stored.Should().StartWith("s1.").And.NotContain("the-trading-secret");
        opener.Open(stored).Should().Be(new VenueCredentials("trading-key-1234", "the-trading-secret"));
        // The request no longer carries the key once it has been decided.
        (await db.ApprovalRequests.Where(r => r.TradingAccountId == accountId).Select(r => r.PayloadJson).SingleAsync()).Should().Be("{}");
    }

    [Fact]
    public async Task StrategyKey_TradesOnlyItsOwnAccounts_WithinItsOwnLimit_AndIsShownOnce()
    {
        (_, _, int own) = await NewAccountAsync();
        (_, _, int other) = await NewAccountAsync();
        HttpResponseMessage created = await _admin.PostAsJsonAsync("/api/strategy-keys", new SaveStrategyKeyRequest("Basis bot", [own], 100_000m), Json);
        StrategyKeyCreatedDto key = (await created.Content.ReadFromJsonAsync<Answer<StrategyKeyCreatedDto>>(Json))!.Value!;
        // A strategy is not a browser: no session, no browser header, only its key.
        HttpClient strategy = _app!.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        strategy.DefaultRequestHeaders.Add("X-KFT-Strategy-Key", key.Secret);

        HttpResponseMessage sent = await strategy.PostAsJsonAsync("/api/strategy/orders", Buy(own, 1m, name: "bot-1"), Json);
        OrderDto order = (await sent.Content.ReadFromJsonAsync<OrderDto>(Json))!;
        await RunEngineAsync();
        OrderDto filled = (await strategy.GetFromJsonAsync<OrderDto>($"/api/strategy/orders/{order.Id}", Json))!;
        HttpResponseMessage elsewhere = await strategy.PostAsJsonAsync("/api/strategy/orders", Buy(other, 1m), Json);
        HttpResponseMessage tooLarge = await strategy.PostAsJsonAsync("/api/strategy/orders", Buy(own, 3m, name: "bot-2"), Json);
        await RunEngineAsync();
        OrderDto rejected = (await strategy.GetFromJsonAsync<OrderDto>($"/api/strategy/orders/{(await tooLarge.Content.ReadFromJsonAsync<OrderDto>(Json))!.Id}", Json))!;
        HttpClient stranger = _app!.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        stranger.DefaultRequestHeaders.Add("X-KFT-Strategy-Key", "kft_" + new string('0', 64));
        await _admin.PostAsJsonAsync($"/api/strategy-keys/{key.Key.Id}/enabled", new { enabled = false }, Json);

        key.Secret.Should().StartWith("kft_").And.HaveLength(68);
        sent.StatusCode.Should().Be(HttpStatusCode.OK, await sent.Content.ReadAsStringAsync());
        order.Source.Should().Be(OrderSource.Strategy);
        order.SubmittedBy.Should().Be("Strategy: Basis bot");
        filled.Status.Should().Be(OrderStatus.Filled);
        elsewhere.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        rejected.Status.Should().Be(OrderStatus.Rejected);
        rejected.RejectReason.Should().Contain("this strategy key's largest order");
        (await stranger.PostAsJsonAsync("/api/strategy/orders", Buy(own, 1m), Json)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await strategy.PostAsJsonAsync("/api/strategy/orders", Buy(own, 1m), Json)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using IServiceScope read = _app!.Services.CreateScope();
        (await read.ServiceProvider.GetRequiredService<FtDbContext>().StrategyKeys.Where(k => k.Id == key.Key.Id).Select(k => k.KeyHash).SingleAsync()).Should().NotContain(key.Secret[4..]);
    }

    [Fact]
    public async Task OrderForTheManager_GoesToTheAccountWithTheBestPriceAfterFees()
    {
        (int fundId, int managerId, int binance) = await NewAccountAsync();
        int bybit = await AddAccountAsync(managerId, Venue.Bybit);

        // Bybit offers at 60,005 against Binance's 60,010; Bybit bids 59,995 against Binance's 59,990.
        OrderDto buy = await TradeAsync(Buy(binance, 1m) with { TradingAccountId = null, ManagerId = managerId });
        OrderDto sell = await TradeAsync(Sell(binance, 1m) with { TradingAccountId = null, ManagerId = managerId });
        BookDto book = (await _admin.GetFromJsonAsync<BookDto>("/api/book/Perp/BTC", Json))!;

        buy.TradingAccountId.Should().Be(bybit);
        buy.AverageFillPrice.Should().Be(60_005m);
        sell.TradingAccountId.Should().Be(bybit);
        book.BestBid.Should().Be(59_995m);
        book.BestBidVenue.Should().Be(Venue.Bybit);
        book.BestAsk.Should().Be(60_005m);
        book.Venues.Select(v => v.Venue).Should().Equal(Venue.Binance, Venue.Bybit);
        (await _admin.GetFromJsonAsync<List<OrderDto>>($"/api/funds/{fundId}/orders", Json))!.Should().HaveCount(2);
    }

    [Fact]
    public async Task Viewer_CanLook_ButCannotTrade_AndATraderCannotChangeKeysOrLimits()
    {
        (int fundId, int managerId, int accountId) = await NewAccountAsync();
        HttpClient viewer = await SignInAsync(Roles.Viewer);
        HttpClient trader = await SignInAsync(Roles.Trader);

        (await viewer.GetAsync($"/api/funds/{fundId}/trading")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewer.GetFromJsonAsync<TradingDto>($"/api/funds/{fundId}/trading", Json))!.CanTrade.Should().BeFalse();
        (await viewer.PostAsJsonAsync("/api/orders", Buy(accountId, 1m), Json)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.PostAsJsonAsync($"/api/funds/{fundId}/halts", new PullHaltRequest(HaltScope.Global, null, null, "x"), Json)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await trader.PostAsJsonAsync("/api/trading-accounts", new SaveTradingAccountRequest(managerId, Venue.Okx, "x", 1m, MarginMode.Cross, 1m, 1m, 100, 0m), Json)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await trader.PostAsJsonAsync($"/api/trading-accounts/{accountId}/key", new TradingKeyRequest("k", "s", null), Json)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await trader.GetAsync("/api/strategy-keys")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task VenueWhoseBookHasGoneQuiet_IsNotTradedOn()
    {
        (_, _, int accountId) = await NewAccountAsync(Venue.Okx);
        Books.Update(new VenueBook(Venue.Okx, "ETH", InstrumentType.Perp, [new(2_999m, 5m)], [new(3_001m, 5m)], DateTimeOffset.UtcNow.AddSeconds(-30)));

        OrderDto order = await TradeAsync(Buy(accountId, 1m) with { Asset = "ETH" });

        order.Status.Should().Be(OrderStatus.Rejected);
        order.RejectReason.Should().Contain("no live market");
    }
}
