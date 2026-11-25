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
    // The books here do not age: a test that runs slowly must not see its market go stale half way through.
    private static readonly StoppedClock BookClock = new();
    private static readonly ConsolidatedBook Books = new(BookClock);
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
            services.AddScoped<WorkingOrderEngine>();
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
        Books.Update(new VenueBook(venue, asset, InstrumentType.Perp, [new(bid, size), new(bid - 1m, size), new(bid - 2m, size)], [new(ask, size), new(ask + 1m, size), new(ask + 2m, size)], BookClock.GetUtcNow()));

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
    public async Task UsdcMarginedAccount_FillsAgainstTheUsdcBook_AndCannotGoLiveYet()
    {
        (int fundId, int managerId, int usdt) = await NewAccountAsync();
        // The same manager and venue again, for the other family of perpetuals.
        HttpResponseMessage added = await _admin.PostAsJsonAsync("/api/trading-accounts", new SaveTradingAccountRequest(managerId, Venue.Binance, "USDC", 3m, MarginMode.Cross, 1_000_000m, 2_000_000m, 100, 1_000_000m, "usdc"), Json);
        added.StatusCode.Should().Be(HttpStatusCode.OK);
        int usdc = (await added.Content.ReadFromJsonAsync<Answer<int>>(Json))!.Value;
        (await _admin.PostAsJsonAsync("/api/trading-accounts", new SaveTradingAccountRequest(managerId, Venue.Binance, "Again", 3m, MarginMode.Cross, 1m, 1m, 100, 1m, "USDC"), Json)).IsSuccessStatusCode.Should().BeFalse();
        (await _admin.PostAsJsonAsync("/api/trading-accounts", new SaveTradingAccountRequest(managerId, Venue.Okx, "No", 3m, MarginMode.Cross, 1m, 1m, 100, 1m, "USDC"), Json)).IsSuccessStatusCode.Should().BeFalse();

        // The USDC book trades a little below the USDT one.
        Books.Update(new VenueBook(Venue.Binance, "BTC", InstrumentType.Perp, [new BookLevel(59_900m, 10m)], [new BookLevel(59_910m, 10m)], BookClock.GetUtcNow(), "USDC"));
        OrderDto onUsdc = await TradeAsync(new SubmitOrderRequest(usdc, null, "BTC", InstrumentType.Perp, OrderSide.Buy, OrderType.Market, TimeInForce.ImmediateOrCancel, 1m, null, false, Guid.NewGuid().ToString("N")));
        OrderDto onUsdt = await TradeAsync(new SubmitOrderRequest(usdt, null, "BTC", InstrumentType.Perp, OrderSide.Buy, OrderType.Market, TimeInForce.ImmediateOrCancel, 1m, null, false, Guid.NewGuid().ToString("N")));

        onUsdc.Status.Should().Be(OrderStatus.Filled);
        onUsdc.AverageFillPrice.Should().Be(59_910m);
        onUsdt.AverageFillPrice.Should().Be(60_010m);
        TradingDto trading = (await _admin.GetFromJsonAsync<TradingDto>($"/api/funds/{fundId}/trading", Json))!;
        trading.Accounts.Single(a => a.Id == usdc).PerpQuote.Should().Be("USDC");
        trading.Accounts.Single(a => a.Id == usdc).Positions.Single().MarkPrice.Should().Be(59_905m);

        HttpResponseMessage live = await _admin.PostAsync($"/api/trading-accounts/{usdc}/go-live", null);
        live.IsSuccessStatusCode.Should().BeFalse();
        (await live.Content.ReadAsStringAsync()).Should().Contain("USDC-margined perpetuals are not connected yet");
        Books.Remove(Venue.Binance, InstrumentType.Perp, "BTC", "USDC");
    }

    [Fact]
    public async Task Pins_AreKeptPerFund_AndOnlyForWhatAVenueLists()
    {
        (int fundId, _, _) = await NewAccountAsync();
        using (IServiceScope scope = _app!.Services.CreateScope())
        {
            FtDbContext db = scope.ServiceProvider.GetRequiredService<FtDbContext>();
            if (!db.ListedInstruments.Any(i => i.Asset == "TIA"))
            {
                db.ListedInstruments.Add(new ListedInstrument { Venue = Venue.Bybit, Type = InstrumentType.Perp, Asset = "TIA", Quote = "USDT", Symbol = "TIAUSDT", Tradable = true, PriceStep = 0.001m, QuantityStep = 0.1m, MinQuantity = 0.1m, SeenAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync();
            }
        }

        (await _admin.PostAsJsonAsync($"/api/funds/{fundId}/pins", new InstrumentRef("tia", InstrumentType.Perp), Json)).StatusCode.Should().Be(HttpStatusCode.OK);
        // Pinning it again changes nothing; pinning what no venue lists is refused.
        (await _admin.PostAsJsonAsync($"/api/funds/{fundId}/pins", new InstrumentRef("TIA", InstrumentType.Perp), Json)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _admin.PostAsJsonAsync($"/api/funds/{fundId}/pins", new InstrumentRef("NOSUCHCOIN", InstrumentType.Perp), Json)).IsSuccessStatusCode.Should().BeFalse();
        HttpClient viewer = await SignInAsync(Roles.Viewer);
        (await viewer.PostAsJsonAsync($"/api/funds/{fundId}/pins", new InstrumentRef("TIA", InstrumentType.Perp), Json)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        TradingDto trading = (await _admin.GetFromJsonAsync<TradingDto>($"/api/funds/{fundId}/trading", Json))!;
        trading.Pinned.Should().Equal(new InstrumentRef("TIA", InstrumentType.Perp));
        (await _admin.GetFromJsonAsync<List<VenueRules>>("/api/instruments/Perp/tia", Json)).Should().Equal(new VenueRules(Venue.Bybit, "USDT", true, 0.001m, 0.1m, 0.1m));

        (await _admin.DeleteAsync($"/api/funds/{fundId}/pins/Perp/TIA")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _admin.GetFromJsonAsync<TradingDto>($"/api/funds/{fundId}/trading", Json))!.Pinned.Should().BeEmpty();
    }

    [Fact]
    public async Task WhatATradingAccountHolds_ReachesTheManagersBook_AsMoneyArrivingNotAsAGain()
    {
        (int fundId, int managerId, int accountId) = await NewAccountAsync();
        await TradeAsync(new SubmitOrderRequest(accountId, null, "BTC", InstrumentType.Perp, OrderSide.Buy, OrderType.Market, TimeInForce.ImmediateOrCancel, 2m, null, false, Guid.NewGuid().ToString("N")));
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        async Task<(decimal Nav, List<string> Lines, List<decimal> Flows)> BookAsync()
        {
            using IServiceScope scope = _app!.Services.CreateScope();
            FtDbContext db = scope.ServiceProvider.GetRequiredService<FtDbContext>();
            (decimal nav, _, _) = await new ManagerBookWriter(db, scope.ServiceProvider.GetRequiredService<IBookFeed>()).WriteAsync(managerId, today, CancellationToken.None);
            List<string> lines = await db.PositionSnapshots.Where(p => p.ManagerAccountId == managerId && p.Date == today).OrderBy(p => p.Instrument.Symbol).Select(p => p.Instrument.Symbol + " " + p.Quantity).ToListAsync();
            return (nav, lines, await db.CashFlows.Where(f => f.ManagerAccountId == managerId && f.IsBookChange).OrderBy(f => f.Id).Select(f => f.AmountUsd).ToListAsync());
        }

        // Paper money is not the fund's money until the fund says it counts.
        (decimal before, List<string> none, _) = await BookAsync();
        before.Should().Be(0m);
        none.Should().BeEmpty();

        (await _admin.PostAsJsonAsync($"/api/funds/{fundId}/paper-in-book", new PaperInBookRequest(true), Json)).StatusCode.Should().Be(HttpStatusCode.OK);
        (decimal counted, List<string> lines, List<decimal> flows) = await BookAsync();

        // A two-bitcoin perpetual with what it has lost since it was bought, and the cash less the fee: the account's own value.
        TradingAccountDto account = (await _admin.GetFromJsonAsync<TradingDto>($"/api/funds/{fundId}/trading", Json))!.Accounts.Single();
        counted.Should().BeApproximately(account.EquityUsd, 0.01m);
        lines.Should().HaveCount(2).And.Contain(l => l.StartsWith("BTC") && l.EndsWith(" 2.00000000"));
        flows.Should().ContainSingle().Which.Should().BeApproximately(account.EquityUsd, 0.01m);

        // Written again, nothing more arrives: the value is booked once.
        (_, _, List<decimal> again) = await BookAsync();
        again.Should().HaveCount(1);

        (await _admin.PostAsJsonAsync($"/api/funds/{fundId}/paper-in-book", new PaperInBookRequest(false), Json)).StatusCode.Should().Be(HttpStatusCode.OK);
        (decimal after, List<string> gone, List<decimal> both) = await BookAsync();
        after.Should().Be(0m);
        gone.Should().BeEmpty();
        both.Sum().Should().Be(0m);

        (await (await SignInAsync(Roles.Trader)).PostAsJsonAsync($"/api/funds/{fundId}/paper-in-book", new PaperInBookRequest(true), Json)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ARealMoneyAccount_IsCountedOnce_ThroughTheReadOnlyKeyWhereThereIsOne()
    {
        (_, int managerId, int accountId) = await NewAccountAsync();
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        using IServiceScope scope = _app!.Services.CreateScope();
        FtDbContext db = scope.ServiceProvider.GetRequiredService<FtDbContext>();
        TradingAccount account = await db.TradingAccounts.SingleAsync(a => a.Id == accountId);
        account.Mode = TradingMode.Live;
        account.LiveEquityUsd = 250_000m;
        db.TradingPositions.Add(new TradingPosition { TradingAccountId = accountId, Asset = "BTC", InstrumentType = InstrumentType.Perp, Quantity = 1m, AveragePrice = 59_000m, UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        ManagerBookWriter writer = new(db, scope.ServiceProvider.GetRequiredService<IBookFeed>());

        // No read-only key on the venue: the trading account is the only thing that sees this money.
        (decimal alone, decimal gross, _) = await writer.WriteAsync(managerId, today, CancellationToken.None);
        alone.Should().Be(250_000m);
        gross.Should().Be(60_000m);

        // A read-only key that has not been read yet counts nothing, so it must not push the account out meanwhile.
        VenueConnection key = new() { ManagerAccountId = managerId, Venue = Venue.Binance, Label = "Read", EncryptedCredentials = "x", KeyHint = "…x", State = ConnectionState.Pending, CreatedAt = DateTimeOffset.UtcNow };
        db.VenueConnections.Add(key);
        await db.SaveChangesAsync();
        (decimal unread, _, _) = await writer.WriteAsync(managerId, today, CancellationToken.None);
        unread.Should().Be(250_000m);

        // Once that key has been read, it reads the same account; counting both would double it.
        key.LastSnapshotJson = JsonSerializer.Serialize(new VenueSnapshot(DateTimeOffset.UtcNow, []), ManagerBookWriter.Json);
        key.State = ConnectionState.Live;
        await db.SaveChangesAsync();
        (decimal withKey, _, _) = await writer.WriteAsync(managerId, today, CancellationToken.None);
        withKey.Should().Be(0m);
        (await db.CashFlows.Where(f => f.ManagerAccountId == managerId && f.IsBookChange).SumAsync(f => f.AmountUsd)).Should().Be(0m);
    }

    // ---------------------------------------------------------------- working orders

    /// <summary>Rounds of the two engines, as the execution service runs them, with the wait between slices taken as passed each time.</summary>
    private async Task<WorkingOrderDto> WorkAsync(long id, int rounds = 1)
    {
        for (int round = 0; round < rounds; round++)
        {
            using IServiceScope scope = _app!.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ExecutionEngine>().RunOnceAsync(CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<WorkingOrderEngine>().RunOnceAsync(CancellationToken.None);
            FtDbContext db = scope.ServiceProvider.GetRequiredService<FtDbContext>();
            await db.WorkingOrders.Where(w => w.Id == id && w.NextSliceAt != null).ExecuteUpdateAsync(w => w.SetProperty(x => x.NextSliceAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }

        await RunEngineAsync();
        return (await _admin.GetFromJsonAsync<WorkingOrderDto>($"/api/working-orders/{id}", Json))!;
    }

    private async Task<WorkingOrderDto> StartWorkingAsync(WorkingOrderRequest request, HttpClient? client = null)
    {
        HttpResponseMessage sent = await (client ?? _admin).PostAsJsonAsync("/api/working-orders", request, Json);
        sent.StatusCode.Should().Be(HttpStatusCode.OK, await sent.Content.ReadAsStringAsync());
        return (await sent.Content.ReadFromJsonAsync<Answer<WorkingOrderDto>>(Json))!.Value!;
    }

    private static WorkingOrderRequest Paced(int? accountId, int? managerId, string asset, decimal quantity, decimal slice, OrderSide side = OrderSide.Buy, decimal? pausePrice = null, int? maxSlippage = null) =>
        new(accountId, managerId, asset, InstrumentType.Perp, side, quantity, null, false, WorkingStyle.Paced, null, null, slice, slice, 1, pausePrice, maxSlippage, null);

    [Fact]
    public async Task PacedOrder_IsWorkedSliceBySlice_AndItsRecordIsTheSumOfItsSlices()
    {
        (int fundId, _, int accountId) = await NewAccountAsync();
        Quote(Venue.Binance, "SOL", 99.9m, 100.1m, 1_000m);

        WorkingOrderDto started = await StartWorkingAsync(Paced(accountId, null, "sol", 30m, 10m));
        started.State.Should().Be(WorkingState.Working);
        started.IsPaper.Should().BeTrue();

        WorkingOrderDto done = await WorkAsync(started.Id, 8);

        done.State.Should().Be(WorkingState.Done);
        done.FilledQuantity.Should().Be(30m);
        done.AverageFillPrice.Should().Be(100.1m);
        done.ArrivalPrice.Should().Be(100m);
        // Bought a tenth of a percent above the middle of the market it started in.
        done.SlippageBps.Should().Be(10m);
        done.SlicesSent.Should().Be(3);
        done.Slices.Should().HaveCount(3).And.OnlyContain(s => s.Status == OrderStatus.Filled && s.Quantity == 10m && s.Venue == Venue.Binance);
        done.FeesUsd.Should().Be(done.Slices!.Sum(s => s.FeesUsd)).And.BePositive();
        (await _admin.GetFromJsonAsync<List<WorkingOrderDto>>($"/api/funds/{fundId}/working-orders", Json))!.Should().ContainSingle(w => w.Id == started.Id && w.Slices == null);

        // The same name again is the same working order, not a second one.
        WorkingOrderRequest named = Paced(accountId, null, "SOL", 5m, 5m) with { ClientId = "again-1" };
        (await StartWorkingAsync(named)).Id.Should().Be((await StartWorkingAsync(named)).Id);
    }

    [Fact]
    public async Task OrderOverTime_SendsEvenSlices_AndMakesUpLostTimeAtNoMoreThanDoubleSpeed()
    {
        (_, _, int accountId) = await NewAccountAsync();
        Quote(Venue.Binance, "LINK", 9.99m, 10.01m, 100_000m);
        WorkingOrderDto started = await StartWorkingAsync(new WorkingOrderRequest(accountId, null, "LINK", InstrumentType.Perp, OrderSide.Sell, 1_000m, null, false, WorkingStyle.OverTime, 10, 10, null, null, null, null, null, null));
        started.PlannedEndAt.Should().BeCloseTo(started.StartedAt!.Value.AddMinutes(10), TimeSpan.FromSeconds(1));
        started.HardEndAt.Should().BeCloseTo(started.StartedAt.Value.AddMinutes(25), TimeSpan.FromSeconds(1));

        WorkingOrderDto first = await WorkAsync(started.Id);
        first.Slices.Should().ContainSingle().Which.Quantity.Should().Be(100m);

        // Five minutes have gone by with one slice sent: it is behind, and catches up two slices' worth at a time.
        using (IServiceScope scope = _app!.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<FtDbContext>().WorkingOrders.Where(w => w.Id == started.Id).ExecuteUpdateAsync(w => w.SetProperty(x => x.StartedAt, DateTimeOffset.UtcNow.AddMinutes(-5)));
        }

        WorkingOrderDto second = await WorkAsync(started.Id);
        second.Slices![0].Quantity.Should().Be(200m);
        second.Side.Should().Be(OrderSide.Sell);
        second.AverageFillPrice.Should().Be(9.99m);
        second.SlippageBps.Should().Be(10m);
    }

    [Fact]
    public async Task EachSlice_GoesToTheManagersAccountWithTheBestPrice()
    {
        (_, int managerId, _) = await NewAccountAsync();
        await AddAccountAsync(managerId, Venue.Bybit);
        Quote(Venue.Binance, "AVAX", 29.9m, 30.2m, 10_000m);
        Quote(Venue.Bybit, "AVAX", 29.9m, 30.0m, 10_000m);
        WorkingOrderDto started = await StartWorkingAsync(Paced(null, managerId, "AVAX", 200m, 100m));
        started.Where.Should().Be("Best venue for each slice");

        (await WorkAsync(started.Id)).Slices.Should().ContainSingle().Which.Venue.Should().Be(Venue.Bybit);

        // The better price moves to the other venue; so does the next slice.
        Quote(Venue.Binance, "AVAX", 29.5m, 29.6m, 10_000m);
        WorkingOrderDto done = await WorkAsync(started.Id, 3);
        done.Slices!.Select(s => s.Venue).Should().BeEquivalentTo([Venue.Bybit, Venue.Binance]);
        done.State.Should().Be(WorkingState.Done);
        done.AverageFillPrice.Should().Be(29.8m);
    }

    [Fact]
    public async Task ItsGuards_HoldItAtThePausePrice_AndPauseItOnSlippage_UntilAPersonSaysCarryOn()
    {
        (_, _, int accountId) = await NewAccountAsync();
        Quote(Venue.Binance, "ADA", 0.499m, 0.501m, 1_000_000m);

        // Buying is held while the market is above the pause price, and carries on by itself when it comes back.
        WorkingOrderDto held = await WorkAsync((await StartWorkingAsync(Paced(accountId, null, "ADA", 2_000m, 1_000m, pausePrice: 0.45m))).Id, 2);
        held.State.Should().Be(WorkingState.Working);
        held.Note.Should().StartWith("Waiting: the market is at 0.501, above the pause price of 0.45");
        held.SlicesSent.Should().Be(0);
        Quote(Venue.Binance, "ADA", 0.448m, 0.449m, 1_000_000m);
        WorkingOrderDto released = await WorkAsync(held.Id);
        released.SlicesSent.Should().Be(1);
        released.Note.Should().BeNull();

        // Its average price is now far better than where it started, so the slippage guard has nothing to say. A
        // fresh order whose price runs away from it is another matter.
        (await _admin.PostAsync($"/api/working-orders/{held.Id}/stop", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await WorkAsync(held.Id)).State.Should().Be(WorkingState.Stopped);

        Quote(Venue.Binance, "ADA", 0.499m, 0.501m, 1_000_000m);
        WorkingOrderDto guarded = await StartWorkingAsync(Paced(accountId, null, "ADA", 3_000m, 1_000m, maxSlippage: 50));
        await WorkAsync(guarded.Id);
        Quote(Venue.Binance, "ADA", 0.519m, 0.521m, 1_000_000m);
        WorkingOrderDto paused = await WorkAsync(guarded.Id, 3);
        paused.State.Should().Be(WorkingState.Paused);
        paused.Note.Should().StartWith(WorkingOrderEngine.SlippageNote);
        paused.SlicesSent.Should().Be(2);

        // A viewer cannot steer it; a trader can. Resumed, it goes on without the guard that has already spoken.
        (await (await SignInAsync(Roles.Viewer)).PostAsync($"/api/working-orders/{guarded.Id}/resume", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await (await SignInAsync(Roles.Trader)).PostAsync($"/api/working-orders/{guarded.Id}/resume", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await WorkAsync(guarded.Id, 3)).State.Should().Be(WorkingState.Done);
    }

    [Fact]
    public async Task PausedByAPerson_ItSendsNothing_AndRefusedThreeTimesRunning_ItStopsItself()
    {
        (_, _, int accountId) = await NewAccountAsync(maxOrder: 2_500m, maxPosition: 2_500m);
        Quote(Venue.Binance, "XRP", 0.999m, 1.001m, 1_000_000m);
        WorkingOrderDto started = await StartWorkingAsync(Paced(accountId, null, "XRP", 10_000m, 1_000m));
        (await _admin.PostAsync($"/api/working-orders/{started.Id}/pause", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        WorkingOrderDto paused = await WorkAsync(started.Id, 3);
        paused.State.Should().Be(WorkingState.Paused);
        paused.SlicesSent.Should().Be(0);
        (await _admin.PostAsync($"/api/working-orders/{started.Id}/pause", null)).IsSuccessStatusCode.Should().BeFalse();

        // Two slices fit under the account's position limit. The third, fourth and fifth are refused, and that ends it.
        (await _admin.PostAsync($"/api/working-orders/{started.Id}/resume", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        WorkingOrderDto stopped = await WorkAsync(started.Id, 12);
        stopped.State.Should().Be(WorkingState.Stopped);
        stopped.Note.Should().StartWith("Stopped: 3 slices in a row were refused.");
        stopped.FilledQuantity.Should().Be(2_000m);
        stopped.Slices!.Count(s => s.Status == OrderStatus.Rejected).Should().Be(3);
        (await _admin.PostAsync($"/api/working-orders/{started.Id}/stop", null)).IsSuccessStatusCode.Should().BeFalse();
    }

    [Fact]
    public async Task PassiveOrder_RestsAtTheBestPrice_FollowsIt_AndTheKillSwitchEndsIt()
    {
        (int fundId, _, int accountId) = await NewAccountAsync();
        Quote(Venue.Binance, "DOGE", 0.0999m, 0.1001m, 1_000_000m);
        WorkingOrderDto started = await StartWorkingAsync(new WorkingOrderRequest(accountId, null, "DOGE", InstrumentType.Perp, OrderSide.Buy, 50_000m, null, false, WorkingStyle.Passive, null, null, null, null, null, null, null, null));

        WorkingOrderDto resting = await WorkAsync(started.Id, 2);
        SliceDto first = resting.Slices.Should().ContainSingle().Subject;
        first.LimitPrice.Should().Be(0.0999m);
        first.Type.Should().Be(OrderType.Limit);
        first.Status.Should().Be(OrderStatus.Working);
        first.Quantity.Should().Be(50_000m);

        // The best bid moves up. Once the slice has rested a moment it is taken back and put at the new best price.
        Quote(Venue.Binance, "DOGE", 0.1000m, 0.1002m, 1_000_000m);
        using (IServiceScope scope = _app!.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<FtDbContext>().Orders.Where(o => o.Id == first.Id).ExecuteUpdateAsync(o => o.SetProperty(x => x.CreatedAt, DateTimeOffset.UtcNow.AddSeconds(-10)));
        }

        WorkingOrderDto moved = await WorkAsync(started.Id, 3);
        moved.Slices.Should().HaveCount(2);
        moved.Slices![1].Status.Should().Be(OrderStatus.Cancelled);
        moved.Slices[0].LimitPrice.Should().Be(0.1000m);
        moved.Slices[0].Status.Should().Be(OrderStatus.Working);

        (await _admin.PostAsJsonAsync($"/api/funds/{fundId}/halts", new PullHaltRequest(HaltScope.Account, null, accountId, "test"), Json)).StatusCode.Should().Be(HttpStatusCode.OK);
        WorkingOrderDto ended = await WorkAsync(started.Id, 3);
        ended.State.Should().Be(WorkingState.Stopped);
        ended.Note.Should().Be("Stopped by the kill switch.");
        ended.Slices.Should().OnlyContain(s => s.Status == OrderStatus.Cancelled);
    }

    [Fact]
    public async Task RealMoneyAboveWhatOneOrderMayBe_WaitsForASecondAdministrator_AndPaperDoesNot()
    {
        (int fundId, _, int accountId) = await NewAccountAsync(maxOrder: 50_000m, maxPosition: 2_000_000m);
        Quote(Venue.Binance, "BNB", 499m, 501m, 100_000m);
        // On paper, a large order needs nobody's leave.
        (await StartWorkingAsync(Paced(accountId, null, "BNB", 400m, 50m))).State.Should().Be(WorkingState.Working);

        using (IServiceScope scope = _app!.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<FtDbContext>().TradingAccounts.Where(a => a.Id == accountId).ExecuteUpdateAsync(a => a.SetProperty(x => x.Mode, TradingMode.Live));
        }

        // Under the limit it starts at once; above it, it waits.
        (await StartWorkingAsync(Paced(accountId, null, "BNB", 80m, 40m))).State.Should().Be(WorkingState.Working);
        WorkingOrderDto waiting = await StartWorkingAsync(Paced(accountId, null, "BNB", 400m, 50m));
        waiting.State.Should().Be(WorkingState.PendingApproval);
        waiting.StartedAt.Should().BeNull();
        (await WorkAsync(waiting.Id, 2)).SlicesSent.Should().Be(0);

        ApprovalDto request = (await _admin.GetFromJsonAsync<TradingDto>($"/api/funds/{fundId}/trading", Json))!.Approvals.Single(a => a.Kind == ApprovalKind.LargeOrder);
        request.Summary.Should().Contain("buy 400 BNB-PERP").And.Contain("above the 50,000 USD one order may be");
        // Not by the one who asked.
        (await _admin.PostAsJsonAsync($"/api/approvals/{request.Id}/approve", new DecisionRequest(null), Json)).IsSuccessStatusCode.Should().BeFalse();
        HttpClient second = await SignInAsync(Roles.Admin);
        (await second.PostAsJsonAsync($"/api/approvals/{request.Id}/approve", new DecisionRequest(null), Json)).StatusCode.Should().Be(HttpStatusCode.OK);
        WorkingOrderDto approved = (await _admin.GetFromJsonAsync<WorkingOrderDto>($"/api/working-orders/{waiting.Id}", Json))!;
        approved.State.Should().Be(WorkingState.Working);
        approved.ApprovedBy.Should().NotBeNull();
        approved.StartedAt.Should().NotBeNull();

        // One that is turned down never starts; one withdrawn takes its request with it.
        WorkingOrderDto refused = await StartWorkingAsync(Paced(accountId, null, "BNB", 300m, 50m));
        ApprovalDto second2 = (await _admin.GetFromJsonAsync<TradingDto>($"/api/funds/{fundId}/trading", Json))!.Approvals.Single(a => a.Kind == ApprovalKind.LargeOrder && a.Status == ApprovalStatus.Pending);
        (await second.PostAsJsonAsync($"/api/approvals/{second2.Id}/reject", new DecisionRequest("No"), Json)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _admin.GetFromJsonAsync<WorkingOrderDto>($"/api/working-orders/{refused.Id}", Json))!.State.Should().Be(WorkingState.Rejected);
        WorkingOrderDto withdrawn = await StartWorkingAsync(Paced(accountId, null, "BNB", 300m, 50m));
        (await _admin.PostAsync($"/api/working-orders/{withdrawn.Id}/stop", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _admin.GetFromJsonAsync<TradingDto>($"/api/funds/{fundId}/trading", Json))!.Approvals.Should().NotContain(a => a.Kind == ApprovalKind.LargeOrder && a.Status == ApprovalStatus.Pending);

        // Back on paper, so the real-money orders left open here are not picked up by another test's engine round.
        using (IServiceScope scope = _app!.Services.CreateScope())
        {
            FtDbContext db = scope.ServiceProvider.GetRequiredService<FtDbContext>();
            await db.WorkingOrders.Where(w => w.TradingAccountId == accountId && (w.State == WorkingState.Working || w.State == WorkingState.Paused)).ExecuteUpdateAsync(w => w.SetProperty(x => x.State, WorkingState.Stopped));
        }
    }

    [Fact]
    public async Task Basket_IsTakenWholeOrNotAtAll_AndOnRealMoneyItsTotalNeedsOneApproval()
    {
        (int fundId, _, int accountId) = await NewAccountAsync(maxOrder: 50_000m, maxPosition: 2_000_000m);
        Quote(Venue.Binance, "ATOM", 9.99m, 10.01m, 1_000_000m);
        Quote(Venue.Binance, "NEAR", 4.99m, 5.01m, 1_000_000m);
        WorkingOrderRequest Line(string asset, OrderSide side, decimal quantity) => new(accountId, null, asset, InstrumentType.Perp, side, quantity, null, false, WorkingStyle.OverTime, 10, 5, null, null, null, null, null, null);

        // One line that cannot be taken refuses all of it, and nothing is recorded.
        HttpResponseMessage bad = await _admin.PostAsJsonAsync("/api/working-orders/basket", new BasketRequest([Line("ATOM", OrderSide.Buy, 1_000m), Line("NOBOOK", OrderSide.Sell, 5m)], "basket-bad"), Json);
        bad.IsSuccessStatusCode.Should().BeFalse();
        (await bad.Content.ReadAsStringAsync()).Should().Contain("Line 2").And.Contain("Nothing was sent");
        (await _admin.GetFromJsonAsync<List<WorkingOrderDto>>($"/api/funds/{fundId}/working-orders", Json)).Should().BeEmpty();
        (await _admin.PostAsJsonAsync("/api/working-orders/basket", new BasketRequest([Line("ATOM", OrderSide.Buy, 1_000m)], null), Json)).IsSuccessStatusCode.Should().BeFalse();
        (await _admin.PostAsJsonAsync("/api/working-orders/basket", new BasketRequest([Line("ATOM", OrderSide.Buy, 1_000m), Line("ATOM", OrderSide.Sell, 10m)], null), Json)).IsSuccessStatusCode.Should().BeFalse();

        // On paper it starts at once, every line carrying the basket's name; sent again, it is the same basket.
        BasketRequest paper = new([Line("ATOM", OrderSide.Buy, 1_000m), Line("NEAR", OrderSide.Sell, 2_000m)], "basket-1");
        HttpResponseMessage sent = await _admin.PostAsJsonAsync("/api/working-orders/basket", paper, Json);
        sent.StatusCode.Should().Be(HttpStatusCode.OK, await sent.Content.ReadAsStringAsync());
        List<WorkingOrderDto> lines = (await sent.Content.ReadFromJsonAsync<Answer<List<WorkingOrderDto>>>(Json))!.Value!;
        lines.Should().HaveCount(2).And.OnlyContain(w => w.State == WorkingState.Working && w.Basket == "basket-1");
        (await (await _admin.PostAsJsonAsync("/api/working-orders/basket", paper, Json)).Content.ReadFromJsonAsync<Answer<List<WorkingOrderDto>>>(Json))!.Value!.Select(w => w.Id).Should().Equal(lines.Select(w => w.Id));
        (await WorkAsync(lines[0].Id)).SlicesSent.Should().Be(1);
        (await WorkAsync(lines[1].Id)).Slices.Should().ContainSingle().Which.Quantity.Should().Be(400m);
        foreach (WorkingOrderDto line in lines)
        {
            await _admin.PostAsync($"/api/working-orders/{line.Id}/stop", null);
            await WorkAsync(line.Id);
        }

        // On real money, each line is under what one order may be, but together they are over it: one request, for the total.
        using (IServiceScope scope = _app!.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<FtDbContext>().TradingAccounts.Where(a => a.Id == accountId).ExecuteUpdateAsync(a => a.SetProperty(x => x.Mode, TradingMode.Live));
        }

        HttpResponseMessage real = await _admin.PostAsJsonAsync("/api/working-orders/basket", new BasketRequest([Line("ATOM", OrderSide.Buy, 4_000m), Line("NEAR", OrderSide.Sell, 6_000m)], "basket-2"), Json);
        List<WorkingOrderDto> waiting = (await real.Content.ReadFromJsonAsync<Answer<List<WorkingOrderDto>>>(Json))!.Value!;
        waiting.Should().OnlyContain(w => w.State == WorkingState.PendingApproval);
        ApprovalDto request = (await _admin.GetFromJsonAsync<TradingDto>($"/api/funds/{fundId}/trading", Json))!.Approvals.Single(a => a.Kind == ApprovalKind.LargeOrder && a.Status == ApprovalStatus.Pending);
        request.Summary.Should().Contain("basket of 2 orders worth about 70,000 USD").And.Contain("buy 4000 ATOM-PERP").And.Contain("sell 6000 NEAR-PERP");

        // Withdrawing one line leaves the request standing for the other; approving it starts what is still waiting.
        (await _admin.PostAsync($"/api/working-orders/{waiting[0].Id}/stop", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await (await SignInAsync(Roles.Admin)).PostAsJsonAsync($"/api/approvals/{request.Id}/approve", new DecisionRequest(null), Json)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _admin.GetFromJsonAsync<WorkingOrderDto>($"/api/working-orders/{waiting[0].Id}", Json))!.State.Should().Be(WorkingState.Stopped);
        (await _admin.GetFromJsonAsync<WorkingOrderDto>($"/api/working-orders/{waiting[1].Id}", Json))!.State.Should().Be(WorkingState.Working);

        using (IServiceScope scope = _app!.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<FtDbContext>().WorkingOrders.Where(w => w.TradingAccountId == accountId && (w.State == WorkingState.Working || w.State == WorkingState.Paused)).ExecuteUpdateAsync(w => w.SetProperty(x => x.State, WorkingState.Stopped));
        }
    }

    [Fact]
    public async Task AnAccountCannotChangeBetweenPaperAndRealMoney_WhileAWorkingOrderIsOpenOnIt()
    {
        (int fundId, int managerId, int accountId) = await NewAccountAsync();
        Quote(Venue.Binance, "DOT", 4.99m, 5.01m, 1_000_000m);
        // Between slices a working order has no open order, and it would carry on with real money unapproved.
        WorkingOrderDto working = await WorkAsync((await StartWorkingAsync(Paced(null, managerId, "DOT", 10_000m, 1_000m))).Id);
        working.SlicesSent.Should().Be(1);
        using (IServiceScope scope = _app!.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<FtDbContext>().TradingAccounts.Where(a => a.Id == accountId).ExecuteUpdateAsync(a => a.SetProperty(x => x.EncryptedKey, "sealed").SetProperty(x => x.KeyHint, "…test"));
        }

        HttpResponseMessage live = await _admin.PostAsync($"/api/trading-accounts/{accountId}/go-live", null);
        live.IsSuccessStatusCode.Should().BeFalse();
        (await live.Content.ReadAsStringAsync()).Should().Contain("working orders");

        // Stopped, the way is clear.
        (await _admin.PostAsync($"/api/working-orders/{working.Id}/stop", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await WorkAsync(working.Id)).State.Should().Be(WorkingState.Stopped);
        (await _admin.PostAsync($"/api/trading-accounts/{accountId}/go-live", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        _ = fundId;
    }

    [Fact]
    public async Task WorkingOrders_GoWhereThePositionIs_AreNotCutBelowTheVenuesStep_AndEndWhenTheirRequestIsWithdrawn()
    {
        (int fundId, int managerId, int binance) = await NewAccountAsync(maxOrder: 50_000m, maxPosition: 2_000_000m);
        int bybit = await AddAccountAsync(managerId, Venue.Bybit);
        Quote(Venue.Binance, "UNI", 9.98m, 10m, 1_000_000m);
        Quote(Venue.Bybit, "UNI", 9.99m, 10.01m, 1_000_000m);
        // Long on Binance. Bybit pays more for a sale, but an order that only reduces has to go where the position is.
        await TradeAsync(new SubmitOrderRequest(binance, null, "UNI", InstrumentType.Perp, OrderSide.Buy, OrderType.Market, TimeInForce.ImmediateOrCancel, 1_000m, null, false, Guid.NewGuid().ToString("N")));
        WorkingOrderDto reducing = await WorkAsync((await StartWorkingAsync(Paced(null, managerId, "UNI", 1_000m, 500m, OrderSide.Sell) with { ReduceOnly = true })).Id, 4);
        reducing.State.Should().Be(WorkingState.Done);
        reducing.Slices.Should().OnlyContain(s => s.Venue == Venue.Binance && s.Status == OrderStatus.Filled);

        // A slice smaller than the venue's smallest step could never be sent: said at once, not found out by waiting.
        using (IServiceScope scope = _app!.Services.CreateScope())
        {
            FtDbContext db = scope.ServiceProvider.GetRequiredService<FtDbContext>();
            if (!db.ListedInstruments.Any(i => i.Asset == "UNI"))
            {
                db.ListedInstruments.Add(new ListedInstrument { Venue = Venue.Binance, Type = InstrumentType.Perp, Asset = "UNI", Quote = "USDT", Symbol = "UNIUSDT", Tradable = true, QuantityStep = 1m, SeenAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync();
            }
        }

        HttpResponseMessage tiny = await _admin.PostAsJsonAsync("/api/working-orders", new WorkingOrderRequest(binance, null, "UNI", InstrumentType.Perp, OrderSide.Buy, 5m, null, false, WorkingStyle.OverTime, 10, 10, null, null, null, null, null, null), Json);
        tiny.IsSuccessStatusCode.Should().BeFalse();
        (await tiny.Content.ReadAsStringAsync()).Should().Contain("below the smallest step");

        // On real money a large order waits; when whoever asked withdraws the request, the order is closed with it.
        using (IServiceScope scope = _app!.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<FtDbContext>().TradingAccounts.Where(a => a.Id == binance || a.Id == bybit).ExecuteUpdateAsync(a => a.SetProperty(x => x.Mode, TradingMode.Live));
        }

        WorkingOrderDto waiting = await StartWorkingAsync(Paced(binance, null, "UNI", 20_000m, 1_000m));
        waiting.State.Should().Be(WorkingState.PendingApproval);
        ApprovalDto request = (await _admin.GetFromJsonAsync<TradingDto>($"/api/funds/{fundId}/trading", Json))!.Approvals.Single(a => a.Kind == ApprovalKind.LargeOrder && a.Status == ApprovalStatus.Pending);
        (await _admin.PostAsync($"/api/approvals/{request.Id}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        WorkingOrderDto closed = (await _admin.GetFromJsonAsync<WorkingOrderDto>($"/api/working-orders/{waiting.Id}", Json))!;
        closed.State.Should().Be(WorkingState.Stopped);
        closed.Note.Should().Contain("withdrawn");
    }

    [Fact]
    public async Task AnAccountThatChangesFromPaperToRealMoney_LeavesTheBookAsTheOneAndArrivesAsTheOther()
    {
        (int fundId, int managerId, int accountId) = await NewAccountAsync();
        (await _admin.PostAsJsonAsync($"/api/funds/{fundId}/paper-in-book", new PaperInBookRequest(true), Json)).StatusCode.Should().Be(HttpStatusCode.OK);
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        using IServiceScope scope = _app!.Services.CreateScope();
        FtDbContext db = scope.ServiceProvider.GetRequiredService<FtDbContext>();
        ManagerBookWriter writer = new(db, scope.ServiceProvider.GetRequiredService<IBookFeed>());
        (decimal onPaper, _, _) = await writer.WriteAsync(managerId, today, CancellationToken.None);
        onPaper.Should().Be(1_000_000m);

        // Now real money, worth far less than the paper balance was. That is not a loss.
        TradingAccount account = await db.TradingAccounts.SingleAsync(a => a.Id == accountId);
        account.Mode = TradingMode.Live;
        account.LiveEquityUsd = 40_000m;
        await db.SaveChangesAsync();
        (decimal real, _, _) = await writer.WriteAsync(managerId, today, CancellationToken.None);
        real.Should().Be(40_000m);
        List<decimal> flows = await db.CashFlows.Where(f => f.ManagerAccountId == managerId && f.IsBookChange).OrderBy(f => f.Id).Select(f => f.AmountUsd).ToListAsync();
        flows.Should().Equal(1_000_000m, -1_000_000m, 40_000m);
    }

    [Theory]
    [InlineData("over time without a time", "Say how long")]
    [InlineData("too many slices", "more often than every five seconds")]
    [InlineData("paced without sizes", "smallest and the largest slice")]
    [InlineData("no market", "no live market")]
    [InlineData("reduce only on spot", "Reduce only is for perpetuals")]
    public async Task WorkingOrderThatMakesNoSense_IsRefusedWithTheReason(string what, string expected)
    {
        (_, _, int accountId) = await NewAccountAsync();
        Quote(Venue.Binance, "SUI", 1.99m, 2.01m, 100_000m);
        WorkingOrderRequest good = Paced(accountId, null, "SUI", 100m, 10m);
        WorkingOrderRequest bad = what switch
        {
            "over time without a time" => good with { Style = WorkingStyle.OverTime, Slices = 10 },
            "too many slices" => good with { Style = WorkingStyle.OverTime, DurationMinutes = 1, Slices = 60 },
            "paced without sizes" => good with { MinSlice = null },
            "no market" => good with { Asset = "NOBOOK" },
            _ => good with { InstrumentType = InstrumentType.Spot, ReduceOnly = true },
        };

        HttpResponseMessage sent = await _admin.PostAsJsonAsync("/api/working-orders", bad, Json);

        sent.IsSuccessStatusCode.Should().BeFalse();
        (await sent.Content.ReadAsStringAsync()).Should().Contain(expected);
    }

    [Fact]
    public async Task TheTicket_ShowsThePosition_AndTheMostTheLimitsAllow_AndThatMuchIsNotRefused()
    {
        (_, int managerId, int accountId) = await NewAccountAsync(maxOrder: 300_000m, maxPosition: 500_000m);
        Quote(Venue.Binance, "LTC", 99.9m, 100.1m, 100_000m);
        async Task<RoomDto> RoomAsync(string query = "") => (await _admin.GetFromJsonAsync<RoomDto>($"/api/room/Perp/ltc?account={accountId}{query}", Json))!;

        // Nothing held: the largest order is what one order may be, with a little kept back for the price moving.
        RoomDto empty = await RoomAsync();
        empty.Held.Should().BeEmpty();
        empty.Buy!.Max.Should().Be(2_985m);
        empty.Buy.LimitedBy.Should().Contain("largest order");
        empty.Buy.MaxWorking.Should().Be(4_975m);
        empty.Sell!.Max.Should().Be(2_985m);

        // Offered, taken, not refused.
        OrderDto bought = await TradeAsync(new SubmitOrderRequest(accountId, null, "LTC", InstrumentType.Perp, OrderSide.Buy, OrderType.Market, TimeInForce.ImmediateOrCancel, empty.Buy.Max, null, false, Guid.NewGuid().ToString("N")));
        bought.Status.Should().Be(OrderStatus.Filled);

        // Long 2,985: buying more is now held back by the position limit, selling by the largest order.
        RoomDto longNow = await RoomAsync();
        HeldDto held = longNow.Held.Should().ContainSingle().Subject;
        held.Quantity.Should().Be(2_985m);
        held.AveragePrice.Should().Be(100.1m);
        held.MarkPrice.Should().Be(100m);
        held.UnrealisedPnlUsd.Should().Be(-298.5m);
        longNow.Buy!.Max.Should().Be(1_990m);
        longNow.Buy.LimitedBy.Should().Contain("largest position");
        longNow.Sell!.Max.Should().Be(2_985m);
        longNow.Sell.MaxWorking.Should().Be(7_960m);
        (await TradeAsync(new SubmitOrderRequest(accountId, null, "LTC", InstrumentType.Perp, OrderSide.Buy, OrderType.Market, TimeInForce.ImmediateOrCancel, longNow.Buy.Max, null, false, Guid.NewGuid().ToString("N")))).Status.Should().Be(OrderStatus.Filled);
        (await RoomAsync()).Buy!.Max.Should().Be(0m);

        // Asked by manager, the answer is for the account an order would go to; an unknown account has no answer.
        (await _admin.GetFromJsonAsync<RoomDto>($"/api/room/Perp/LTC?manager={managerId}", Json))!.Held.Single().TradingAccountId.Should().Be(accountId);
        (await _admin.GetAsync("/api/room/Perp/LTC?account=999999")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        // With no live price there is nothing to size an order with.
        RoomDto unpriced = (await _admin.GetFromJsonAsync<RoomDto>($"/api/room/Perp/NOBOOK?account={accountId}", Json))!;
        unpriced.Buy.Should().BeNull();
        unpriced.Sell.Should().BeNull();
    }

    [Fact]
    public async Task InstrumentSearch_FindsWhatTheVenuesList_TheExactNameFirst()
    {
        using (IServiceScope scope = _app!.Services.CreateScope())
        {
            FtDbContext db = scope.ServiceProvider.GetRequiredService<FtDbContext>();
            if (!db.ListedInstruments.Any())
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                ListedInstrument Listed(Venue venue, InstrumentType type, string asset, bool tradable = true) => new() { Venue = venue, Type = type, Asset = asset, Quote = "USDT", Symbol = asset + "USDT", Tradable = tradable, SeenAt = now };
                db.ListedInstruments.AddRange(
                    Listed(Venue.Binance, InstrumentType.Spot, "PENDLE"), Listed(Venue.Bybit, InstrumentType.Spot, "PENDLE"), Listed(Venue.Okx, InstrumentType.Spot, "PENDLE"),
                    Listed(Venue.Binance, InstrumentType.Perp, "PENDLE"), Listed(Venue.Bybit, InstrumentType.Perp, "PENDLE"),
                    Listed(Venue.Gate, InstrumentType.Spot, "PEN"), Listed(Venue.Mexc, InstrumentType.Spot, "PENDULUM"),
                    // Listed, but the venue is not taking orders for it: not offered.
                    Listed(Venue.Binance, InstrumentType.Spot, "PENGONE", tradable: false));
                await db.SaveChangesAsync();
            }
        }

        List<InstrumentHit> hits = (await _admin.GetFromJsonAsync<List<InstrumentHit>>("/api/instruments?q=pen", Json))!;

        hits.Select(h => $"{h.Asset} {h.Type} {h.Venues.Count}").Should().Equal("PEN Spot 1", "PENDLE Spot 3", "PENDLE Perp 2", "PENDULUM Spot 1");
        (await _admin.GetFromJsonAsync<List<InstrumentHit>>("/api/instruments?q=", Json)).Should().BeEmpty();
        (await TestDatabase.CreateClient(_app!).GetAsync("/api/instruments?q=pen")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
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
        Books.Update(new VenueBook(Venue.Binance, "BTC", InstrumentType.Perp, [new(59_960m, 3m)], [new(59_975m, 3m), new(59_990m, 3m)], BookClock.GetUtcNow()));
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
        Books.Update(new VenueBook(Venue.Okx, "ETH", InstrumentType.Perp, [new(2_999m, 5m)], [new(3_001m, 5m)], BookClock.GetUtcNow().AddSeconds(-30)));

        OrderDto order = await TradeAsync(Buy(accountId, 1m) with { Asset = "ETH" });

        order.Status.Should().Be(OrderStatus.Rejected);
        order.RejectReason.Should().Contain("no live market");
    }

    private sealed class StoppedClock : TimeProvider
    {
        private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
