using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using KatalepsisFT.Application;
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
using Microsoft.Extensions.Logging.Abstractions;

namespace KatalepsisFT.Web.Tests.Execution;

/// <summary>
/// Real orders, against a stand-in for the venue that can be made to fill, refuse, cancel, or simply not answer.
/// What is tested is what must hold with real money: an order goes out once and only once, its record is what the
/// venue reports, and not knowing never turns into assuming.
/// </summary>
public class LiveExecutionTests(TestDatabase database) : IClassFixture<TestDatabase>, IAsyncLifetime
{
    private const string ExecMasterKey = "an-execution-master-key-that-is-long-enough-123";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly MovableClock Clock = new();
    private static readonly ConsolidatedBook Books = new(Clock);
    private static readonly FakeVenue KucoinReader = new(Venue.Kucoin, needsPassphrase: true) { Check = new CredentialCheck(true, false) };
    private static WebApplicationFactory<Program>? _app;

    private readonly FakeGateway _venue = new();
    private readonly LivePacing _pacing = new();
    private HttpClient _admin = null!;
    private HttpClient _second = null!;

    private sealed record Answer<T>(T? Value, string? Notice);

    private sealed class MovableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Keys : ITradingKeys
    {
        public bool Ready => true;

        public VenueCredentials Open(string sealedKey) => new("opened", "opened", "opened");
    }

    /// <summary>The venue: it keeps the orders it was sent and answers as each test tells it to.</summary>
    private sealed class FakeGateway : IOrderGateway
    {
        public Venue Venue => Venue.Kucoin;

        public VenueInstrument? Rules { get; set; } = new("BTC-USDT", 0.001m, 0.1m, 0.001m, 5m);

        public List<VenueOrderRequest> Placed { get; } = [];

        public List<string> Cancels { get; } = [];

        public Dictionary<string, VenueOrderReport> Known { get; } = [];

        /// <summary>What to do when an order arrives: null takes it, an exception is thrown as the answer.</summary>
        public Exception? OnPlace { get; set; }

        /// <summary>Whether an order whose answer was "unclear" did in fact arrive.</summary>
        public bool ArrivesAnyway { get; set; }

        public Exception? OnFind { get; set; }

        public VenueAccountReport? Account { get; set; }

        public Exception? OnAccount { get; set; }

        public Task<VenueInstrument?> InstrumentAsync(string asset, InstrumentType type, CancellationToken cancellationToken) => Task.FromResult(Rules);

        public Task<string?> PlaceAsync(VenueCredentials credentials, VenueInstrument instrument, VenueOrderRequest order, CancellationToken cancellationToken)
        {
            Placed.Add(order);
            if (OnPlace is null || ArrivesAnyway)
            {
                Known[order.ClientId] = new VenueOrderReport(VenueOrderState.Open, 0m, 0m, null, "v-" + Placed.Count);
            }

            return OnPlace is null ? Task.FromResult<string?>("v-" + Placed.Count) : throw OnPlace;
        }

        public Task CancelAsync(VenueCredentials credentials, VenueInstrument instrument, InstrumentType type, OrderNames order, CancellationToken cancellationToken)
        {
            string clientId = order.ClientId;
            Cancels.Add(clientId);
            if (Known.TryGetValue(clientId, out VenueOrderReport? report) && report.State == VenueOrderState.Open)
            {
                Known[clientId] = report with { State = VenueOrderState.Done };
            }

            return Task.CompletedTask;
        }

        public Task<VenueOrderReport> FindAsync(VenueCredentials credentials, VenueInstrument instrument, InstrumentType type, OrderNames order, CancellationToken cancellationToken) =>
            OnFind is not null ? throw OnFind : Task.FromResult(Known.GetValueOrDefault(order.ClientId) ?? new VenueOrderReport(VenueOrderState.NotFound, 0m, 0m, null, null));

        public Task<VenueAccountReport> AccountAsync(VenueCredentials credentials, CancellationToken cancellationToken) =>
            OnAccount is not null ? throw OnAccount : Task.FromResult(Account ?? new VenueAccountReport(0m, null, []));

        /// <summary>The venue trades some of an order: so much in all, for so much in all.</summary>
        public void Trade(string clientId, decimal filled, decimal value, decimal? fee, bool done) =>
            Known[clientId] = new VenueOrderReport(done ? VenueOrderState.Done : VenueOrderState.Open, filled, value, fee, Known[clientId].VenueOrderId);
    }

    public async Task InitializeAsync()
    {
        _app ??= database.CreateApp(services: services =>
        {
            foreach (ServiceDescriptor descriptor in services.Where(s => s.ServiceType == typeof(IBookFeed) || s.ServiceType == typeof(IExecutionSignal) || s.ServiceType == typeof(IVenueAdapter)).ToList())
            {
                services.Remove(descriptor);
            }

            services.AddSingleton(Books);
            services.AddSingleton<IBookFeed, LocalBookFeed>();
            services.AddSingleton<IExecutionSignal, NoExecutionSignal>();
            services.AddSingleton<IVenueAdapter>(KucoinReader);
            // Two readers that answer "the key works" and nothing about what it may do.
            services.AddSingleton<IVenueAdapter>(new FakeVenue(Venue.Mexc) { Check = new CredentialCheck(null, null) });
            services.AddSingleton<IVenueAdapter>(new FakeVenue(Venue.Okx, needsPassphrase: true) { Check = new CredentialCheck(null, null) });
            services.AddScoped<ExecutionEngine>();
        });
        using (IServiceScope scope = _app.Services.CreateScope())
        {
            await SealedTradingVault.EnsureKeysAsync(scope.ServiceProvider.GetRequiredService<FtDbContext>(), ExecMasterKey, CancellationToken.None);
        }

        _admin = await SignInAsync(Roles.Admin);
        _second = await SignInAsync(Roles.Admin);
        Quote(60_000m);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static void Quote(decimal mid, InstrumentType type = InstrumentType.Spot) =>
        Books.Update(new VenueBook(Venue.Kucoin, "BTC", type, [new(mid - 5m, 10m)], [new(mid + 5m, 10m)], Clock.GetUtcNow()));

    private async Task<HttpClient> SignInAsync(string role)
    {
        string email = await database.AddUserAsync(_app!, role);
        HttpClient client = TestDatabase.CreateClient(_app!);
        await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, TestDatabase.Password, false));
        return client;
    }

    /// <summary>One step of the service that works real orders, as its loop would take it.</summary>
    private async Task<bool> StepAsync(long orderId)
    {
        using IServiceScope scope = _app!.Services.CreateScope();
        return await new LiveExecution(scope.ServiceProvider.GetRequiredService<IFtDbContext>(), [_venue], new Keys(), Books, _pacing, Clock, NullLogger<LiveExecution>.Instance).HandleAsync(orderId, CancellationToken.None);
    }

    private async Task ReadAccountAsync(int accountId)
    {
        using IServiceScope scope = _app!.Services.CreateScope();
        await new LiveExecution(scope.ServiceProvider.GetRequiredService<IFtDbContext>(), [_venue], new Keys(), Books, _pacing, Clock, NullLogger<LiveExecution>.Instance).ReadAccountAsync(accountId, CancellationToken.None);
    }

    /// <summary>A fund with a KuCoin account that has a key but still trades on paper.</summary>
    private async Task<(int FundId, int AccountId)> NewAccountAsync(decimal maxOrder = 50_000m, int band = 100)
    {
        NewFundRequest request = new($"Live {Guid.NewGuid():N}", null, [new NewSleeveRequest("Core", MandateType.Directional, [new NewManagerRequest("Desk", 0m)])], FundKind.Fund, null);
        int fundId = (await (await _admin.PostAsJsonAsync("/api/funds", request, Json)).Content.ReadFromJsonAsync<FundCreatedDto>(Json))!.Id;
        int managerId = (await _admin.GetFromJsonAsync<StructureDto>($"/api/funds/{fundId}/structure", Json))!.Sleeves[0].Managers[0].Id;
        HttpResponseMessage added = await _admin.PostAsJsonAsync("/api/trading-accounts", new SaveTradingAccountRequest(managerId, Venue.Kucoin, "Main", 3m, MarginMode.Cross, maxOrder, 200_000m, band, 1_000_000m), Json);
        added.StatusCode.Should().Be(HttpStatusCode.OK, await added.Content.ReadAsStringAsync());
        int accountId = (await added.Content.ReadFromJsonAsync<Answer<int>>(Json))!.Value;
        (await _admin.PostAsJsonAsync($"/api/trading-accounts/{accountId}/key", new TradingKeyRequest("kucoin-key-9876", "secret", "phrase"), Json)).StatusCode.Should().Be(HttpStatusCode.OK);
        await ApproveAsync(fundId);
        return (fundId, accountId);
    }

    private async Task ApproveAsync(int fundId)
    {
        int requestId = (await _admin.GetFromJsonAsync<TradingDto>($"/api/funds/{fundId}/trading", Json))!.Approvals.Single(a => a.Status == ApprovalStatus.Pending).Id;
        HttpResponseMessage approved = await _second.PostAsJsonAsync($"/api/approvals/{requestId}/approve", new DecisionRequest(null), Json);
        approved.StatusCode.Should().Be(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync());
    }

    private async Task<(int FundId, int AccountId)> NewLiveAccountAsync(decimal maxOrder = 50_000m, int band = 100)
    {
        (int fundId, int accountId) = await NewAccountAsync(maxOrder, band);
        (await _admin.PostAsync($"/api/trading-accounts/{accountId}/go-live", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        await ApproveAsync(fundId);
        return (fundId, accountId);
    }

    private async Task<long> SendAsync(int accountId, OrderSide side, decimal quantity, decimal? limit = null, TimeInForce tif = TimeInForce.GoodTillCancelled, InstrumentType type = InstrumentType.Spot)
    {
        SubmitOrderRequest request = new(accountId, null, "BTC", type, side, limit is null ? OrderType.Market : OrderType.Limit, limit is null ? TimeInForce.ImmediateOrCancel : tif, quantity, limit, false, null);
        HttpResponseMessage sent = await _admin.PostAsJsonAsync("/api/orders", request, Json);
        sent.StatusCode.Should().Be(HttpStatusCode.OK, await sent.Content.ReadAsStringAsync());
        return (await sent.Content.ReadFromJsonAsync<Answer<OrderDto>>(Json))!.Value!.Id;
    }

    private async Task<OrderDto> OrderAsync(long id) => (await _admin.GetFromJsonAsync<OrderDto>($"/api/orders/{id}", Json))!;

    private async Task<TradingAccountDto> AccountAsync(int fundId) => (await _admin.GetFromJsonAsync<TradingDto>($"/api/funds/{fundId}/trading", Json))!.Accounts.Single();

    [Fact]
    public async Task GoingLive_NeedsAKeyAndASecondAdministrator_AndStartsFromACleanSheet()
    {
        (int fundId, int accountId) = await NewAccountAsync();
        using (IServiceScope scope = _app!.Services.CreateScope())
        {
            // Something left over from paper trading.
            FtDbContext db = scope.ServiceProvider.GetRequiredService<FtDbContext>();
            db.TradingPositions.Add(new TradingPosition { TradingAccountId = accountId, Asset = "BTC", InstrumentType = InstrumentType.Spot, Quantity = 2m, AveragePrice = 50_000m });
            await db.SaveChangesAsync();
        }

        TradingAccountDto before = await AccountAsync(fundId);
        HttpResponseMessage asked = await _admin.PostAsync($"/api/trading-accounts/{accountId}/go-live", null);
        HttpResponseMessage twice = await _admin.PostAsync($"/api/trading-accounts/{accountId}/go-live", null);
        ApprovalDto request = (await _admin.GetFromJsonAsync<TradingDto>($"/api/funds/{fundId}/trading", Json))!.Approvals.Single(a => a.Status == ApprovalStatus.Pending);
        HttpResponseMessage own = await _admin.PostAsJsonAsync($"/api/approvals/{request.Id}/approve", new DecisionRequest(null), Json);
        TradingAccountDto waiting = await AccountAsync(fundId);
        await ApproveAsync(fundId);
        TradingAccountDto live = await AccountAsync(fundId);
        HttpResponseMessage back = await _admin.PostAsync($"/api/trading-accounts/{accountId}/paper", null);

        before.CanGoLive.Should().BeTrue();
        before.Mode.Should().Be(TradingMode.Paper);
        asked.StatusCode.Should().Be(HttpStatusCode.OK, await asked.Content.ReadAsStringAsync());
        twice.StatusCode.Should().Be(HttpStatusCode.Conflict);
        request.Kind.Should().Be(ApprovalKind.GoLive);
        request.Summary.Should().Contain("real money").And.Contain("50,000");
        own.StatusCode.Should().Be(HttpStatusCode.Conflict);
        waiting.Mode.Should().Be(TradingMode.Paper);
        live.Mode.Should().Be(TradingMode.Live);
        live.Positions.Should().BeEmpty();
        live.CanGoLive.Should().BeFalse();
        back.StatusCode.Should().Be(HttpStatusCode.OK);
        (await AccountAsync(fundId)).Mode.Should().Be(TradingMode.Paper);
    }

    [Fact]
    public async Task AccountWithoutAKey_OrOnAVenueWithoutRealOrders_CannotGoLive()
    {
        NewFundRequest request = new($"NoKey {Guid.NewGuid():N}", null, [new NewSleeveRequest("Core", MandateType.Directional, [new NewManagerRequest("Desk", 0m)])], FundKind.Fund, null);
        int fundId = (await (await _admin.PostAsJsonAsync("/api/funds", request, Json)).Content.ReadFromJsonAsync<FundCreatedDto>(Json))!.Id;
        int managerId = (await _admin.GetFromJsonAsync<StructureDto>($"/api/funds/{fundId}/structure", Json))!.Sleeves[0].Managers[0].Id;
        int kucoin = (await (await _admin.PostAsJsonAsync("/api/trading-accounts", new SaveTradingAccountRequest(managerId, Venue.Kucoin, "Main", 3m, MarginMode.Cross, 1_000m, 5_000m, 100, 0m), Json)).Content.ReadFromJsonAsync<Answer<int>>(Json))!.Value;
        int paperOnly = (await (await _admin.PostAsJsonAsync("/api/trading-accounts", new SaveTradingAccountRequest(managerId, Venue.Hyperliquid, "Main", 3m, MarginMode.Cross, 1_000m, 5_000m, 100, 0m), Json)).Content.ReadFromJsonAsync<Answer<int>>(Json))!.Value;

        HttpResponseMessage noKey = await _admin.PostAsync($"/api/trading-accounts/{kucoin}/go-live", null);
        HttpResponseMessage noVenue = await _admin.PostAsync($"/api/trading-accounts/{paperOnly}/go-live", null);

        (await noKey.Content.ReadFromJsonAsync<ErrorResponse>(Json))!.Message.Should().Contain("trading key first");
        (await noVenue.Content.ReadFromJsonAsync<ErrorResponse>(Json))!.Message.Should().Contain("Real orders cannot be sent to").And.Contain("yet");
    }

    [Fact]
    public async Task MarketOrder_GoesOutOnceAsAPricedOrder_AndItsRecordIsWhatTheVenueReports()
    {
        (int fundId, int accountId) = await NewLiveAccountAsync();
        long id = await SendAsync(accountId, OrderSide.Buy, 0.25049m);

        // The paper engine leaves it alone: a real order is never "filled" against the book.
        using (IServiceScope scope = _app!.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ExecutionEngine>().RunOnceAsync(CancellationToken.None);
        }

        OrderDto untouched = await OrderAsync(id);
        await StepAsync(id);
        VenueOrderRequest sent = _venue.Placed.Single();
        OrderDto working = await OrderAsync(id);
        _venue.Trade(sent.ClientId, 0.1m, 6_000.5m, 6.0005m, done: false);
        Clock.Now += TimeSpan.FromSeconds(1);
        await StepAsync(id);
        OrderDto partly = await OrderAsync(id);
        _venue.Trade(sent.ClientId, 0.25m, 15_002m, 15.002m, done: true);
        Clock.Now += TimeSpan.FromSeconds(1);
        await StepAsync(id);
        (await StepAsync(id)).Should().BeFalse();
        OrderDto filled = await OrderAsync(id);

        untouched.Status.Should().Be(OrderStatus.Received);
        // Cut down to the venue's step, priced at the edge of the account's 1% band, to trade now or not at all.
        sent.Quantity.Should().Be(0.25m);
        sent.OrderType.Should().Be(OrderType.Limit);
        sent.TimeInForce.Should().Be(TimeInForce.ImmediateOrCancel);
        sent.LimitPrice.Should().Be(60_600m);
        sent.ClientId.Should().StartWith($"kft{id}x").And.HaveLength($"kft{id}x".Length + 12).And.MatchRegex("^[a-z0-9]+$");
        working.Status.Should().Be(OrderStatus.Working);
        working.Quantity.Should().Be(0.25m);
        working.Events.Select(e => e.Kind).Should().Equal(OrderEventKind.Received, OrderEventKind.Note, OrderEventKind.Accepted, OrderEventKind.Working);
        partly.Status.Should().Be(OrderStatus.PartlyFilled);
        partly.FilledQuantity.Should().Be(0.1m);
        partly.AverageFillPrice.Should().Be(60_005m);
        filled.Status.Should().Be(OrderStatus.Filled);
        filled.AverageFillPrice.Should().Be(60_008m);
        filled.FeesUsd.Should().Be(15.002m);
        // The second fill is what the venue's totals imply: 0.15 for 9,001.50.
        filled.Events.Where(e => e.Kind == OrderEventKind.Fill).Select(e => (e.Quantity, e.Price, e.FeeUsd)).Should().Equal((0.1m, 60_005m, 6.0005m), (0.15m, 60_010m, 9.0015m));
        _venue.Placed.Should().HaveCount(1);
        TradingPositionDto position = (await AccountAsync(fundId)).Positions.Single();
        position.Quantity.Should().Be(0.25m);
        position.AveragePrice.Should().Be(60_008m);
    }

    [Fact]
    public async Task OrderTheVenueRefuses_IsRejectedWithTheVenuesReason_AndNothingIsOnTheBooks()
    {
        (int fundId, int accountId) = await NewLiveAccountAsync();
        _venue.OnPlace = new VenueRefusedException("Balance insufficient! (code 200004)");

        long id = await SendAsync(accountId, OrderSide.Buy, 0.1m, 59_900m);
        await StepAsync(id);
        OrderDto order = await OrderAsync(id);

        order.Status.Should().Be(OrderStatus.Rejected);
        order.RejectReason.Should().Be("KuCoin refused the order: Balance insufficient! (code 200004)");
        order.FilledQuantity.Should().Be(0m);
        (await AccountAsync(fundId)).Positions.Should().BeEmpty();
        (await StepAsync(id)).Should().BeFalse();
        _venue.Placed.Should().HaveCount(1);
    }

    [Fact]
    public async Task OrderWhoseAnswerWasLost_IsNeverSentAgain_AndIsFoundAtTheVenueIfItArrived()
    {
        (_, int accountId) = await NewLiveAccountAsync();
        _venue.OnPlace = new VenueUnclearException("no answer in time");
        _venue.ArrivesAnyway = true;

        long id = await SendAsync(accountId, OrderSide.Buy, 0.1m, 59_900m);
        await StepAsync(id);
        OrderDto unanswered = await OrderAsync(id);
        // The venue cannot be reached for a while: not knowing changes nothing.
        _venue.OnFind = new VenueUnclearException("the connection failed");
        Clock.Now += TimeSpan.FromSeconds(45);
        await StepAsync(id);
        OrderDto stillUnanswered = await OrderAsync(id);
        _venue.OnFind = null;
        Clock.Now += TimeSpan.FromSeconds(1);
        await StepAsync(id);
        OrderDto found = await OrderAsync(id);

        unanswered.Status.Should().Be(OrderStatus.Accepted);
        unanswered.Events[^1].Kind.Should().Be(OrderEventKind.Note);
        unanswered.Events[^1].Detail.Should().Contain("not sent again");
        stillUnanswered.Status.Should().Be(OrderStatus.Accepted);
        found.Status.Should().Be(OrderStatus.Working);
        found.Events[^1].Detail.Should().Be("Found at KuCoin: it did arrive.");
        _venue.Placed.Should().HaveCount(1);
    }

    [Fact]
    public async Task OrderThatNeverArrived_IsClosedAsSuch_OnlyOnceTheVenueSaysSoAndTimeHasPassed()
    {
        (_, int accountId) = await NewLiveAccountAsync();
        _venue.OnPlace = new VenueUnclearException("the connection failed");

        long id = await SendAsync(accountId, OrderSide.Buy, 0.1m, 59_900m);
        await StepAsync(id);
        Clock.Now += TimeSpan.FromSeconds(10);
        await StepAsync(id);
        OrderDto early = await OrderAsync(id);
        Clock.Now += TimeSpan.FromSeconds(25);
        await StepAsync(id);
        OrderDto late = await OrderAsync(id);

        // Ten seconds in, "not there" could still mean "not there yet".
        early.Status.Should().Be(OrderStatus.Accepted);
        late.Status.Should().Be(OrderStatus.Rejected);
        late.RejectReason.Should().Contain("never arrived").And.Contain("Nothing was traded");
        _venue.Placed.Should().HaveCount(1);
    }

    [Fact]
    public async Task Cancel_IsPassedToTheVenue_AndTheOrderIsClosedWhenTheVenueSaysItIsGone()
    {
        (_, int accountId) = await NewLiveAccountAsync();
        long id = await SendAsync(accountId, OrderSide.Buy, 0.2m, 59_500m);
        await StepAsync(id);
        string name = _venue.Placed.Single().ClientId;
        _venue.Trade(name, 0.05m, 2_975m, 2.975m, done: false);
        Clock.Now += TimeSpan.FromSeconds(1);
        await StepAsync(id);

        (await _admin.PostAsync($"/api/orders/{id}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        Clock.Now += TimeSpan.FromSeconds(1);
        await StepAsync(id);
        OrderDto cancelled = await OrderAsync(id);

        _venue.Cancels.Should().Equal(name);
        cancelled.Status.Should().Be(OrderStatus.Cancelled);
        cancelled.FilledQuantity.Should().Be(0.05m);
        cancelled.Events.Select(e => e.Kind).Should().ContainInOrder(OrderEventKind.Fill, OrderEventKind.CancelRequested, OrderEventKind.Cancelled);
        cancelled.Events[^1].Detail.Should().Be("Cancelled on request.");
    }

    [Fact]
    public async Task OrderTheVenueOnceTookAndNoLongerHas_IsClosedAsCancelled_OnlyAfterItHasBeenMissingForAWhile()
    {
        (_, int accountId) = await NewLiveAccountAsync();
        long id = await SendAsync(accountId, OrderSide.Buy, 0.2m, 59_500m);
        await StepAsync(id);
        string name = _venue.Placed.Single().ClientId;
        _venue.Trade(name, 0.05m, 2_975m, 2.975m, done: false);
        Clock.Now += TimeSpan.FromSeconds(1);
        await StepAsync(id);

        // Some venues forget an order once it is cancelled with nothing more filled.
        _venue.Known.Remove(name);
        Clock.Now += TimeSpan.FromSeconds(1);
        await StepAsync(id);
        Clock.Now += TimeSpan.FromSeconds(5);
        await StepAsync(id);
        OrderDto missingBriefly = await OrderAsync(id);
        Clock.Now += TimeSpan.FromSeconds(6);
        await StepAsync(id);
        OrderDto gone = await OrderAsync(id);

        missingBriefly.Status.Should().Be(OrderStatus.PartlyFilled);
        gone.Status.Should().Be(OrderStatus.Cancelled);
        gone.FilledQuantity.Should().Be(0.05m);
        gone.Events[^1].Detail.Should().Be("Cancelled at KuCoin: it no longer has the order.");
    }

    [Fact]
    public async Task KeyOnAVenueThatCannotSayWhatAKeyMayDo_IsAccepted_WithTheApproverToldItWasNotChecked()
    {
        NewFundRequest request = new($"Unchecked {Guid.NewGuid():N}", null, [new NewSleeveRequest("Core", MandateType.Directional, [new NewManagerRequest("Desk", 0m)])], FundKind.Fund, null);
        int fundId = (await (await _admin.PostAsJsonAsync("/api/funds", request, Json)).Content.ReadFromJsonAsync<FundCreatedDto>(Json))!.Id;
        int managerId = (await _admin.GetFromJsonAsync<StructureDto>($"/api/funds/{fundId}/structure", Json))!.Sleeves[0].Managers[0].Id;
        int mexc = (await (await _admin.PostAsJsonAsync("/api/trading-accounts", new SaveTradingAccountRequest(managerId, Venue.Mexc, "Main", 1m, MarginMode.Cross, 1_000m, 5_000m, 100, 0m), Json)).Content.ReadFromJsonAsync<Answer<int>>(Json))!.Value;
        int okx = (await (await _admin.PostAsJsonAsync("/api/trading-accounts", new SaveTradingAccountRequest(managerId, Venue.Okx, "Main", 1m, MarginMode.Cross, 1_000m, 5_000m, 100, 0m), Json)).Content.ReadFromJsonAsync<Answer<int>>(Json))!.Value;

        HttpResponseMessage accepted = await _admin.PostAsJsonAsync($"/api/trading-accounts/{mexc}/key", new TradingKeyRequest("mexc-key-4321", "secret", null), Json);
        HttpResponseMessage refused = await _admin.PostAsJsonAsync($"/api/trading-accounts/{okx}/key", new TradingKeyRequest("okx-key-4321", "secret", "phrase"), Json);
        ApprovalDto asked = (await _admin.GetFromJsonAsync<TradingDto>($"/api/funds/{fundId}/trading", Json))!.Approvals.Single();

        accepted.StatusCode.Should().Be(HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync());
        (await accepted.Content.ReadFromJsonAsync<Answer<int>>(Json))!.Notice.Should().Contain("cannot say what it may do").And.Contain("make sure it cannot withdraw");
        asked.Summary.Should().Contain("NOT CHECKED").And.Contain("withdrawal is switched off");
        // A venue that can say, and for once did not, is still refused.
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await refused.Content.ReadFromJsonAsync<ErrorResponse>(Json))!.Message.Should().Contain("did not say what this key may do");
    }

    [Fact]
    public async Task KillSwitch_CancelsWhatIsAtTheVenue_AndStopsNewOrdersBeforeTheyLeave()
    {
        (int fundId, int accountId) = await NewLiveAccountAsync();
        long resting = await SendAsync(accountId, OrderSide.Buy, 0.2m, 59_500m);
        await StepAsync(resting);

        (await _admin.PostAsJsonAsync($"/api/funds/{fundId}/halts", new PullHaltRequest(HaltScope.Account, null, accountId, "Stop"), Json)).StatusCode.Should().Be(HttpStatusCode.OK);
        Clock.Now += TimeSpan.FromSeconds(1);
        await StepAsync(resting);
        long blocked = await SendAsync(accountId, OrderSide.Buy, 0.1m, 59_500m);
        await StepAsync(blocked);

        (await OrderAsync(resting)).Events[^1].Detail.Should().Be("Cancelled by the kill switch.");
        (await OrderAsync(blocked)).Status.Should().Be(OrderStatus.Rejected);
        _venue.Placed.Should().HaveCount(1);
        _venue.Cancels.Should().HaveCount(1);
    }

    [Theory]
    [InlineData("too small for the venue", "is too small")]
    [InlineData("worth too little", "smallest order")]
    [InlineData("not listed", "does not list BTC")]
    [InlineData("over the limit", "largest order")]
    [InlineData("no market", "no live market")]
    public async Task OrderThatCannotBeSent_IsRejectedBeforeAnythingLeaves(string what, string expected)
    {
        (_, int accountId) = await NewLiveAccountAsync(maxOrder: 10_000m);
        if (what == "not listed")
        {
            _venue.Rules = null;
        }

        if (what == "worth too little")
        {
            _venue.Rules = _venue.Rules! with { MinNotional = 100m };
        }

        if (what == "no market")
        {
            Books.Clear(Venue.Kucoin);
        }

        long id = await SendAsync(accountId, OrderSide.Buy, what == "too small for the venue" ? 0.0004m : what == "over the limit" ? 0.5m : 0.001m, 59_900m);
        await StepAsync(id);
        OrderDto order = await OrderAsync(id);
        Quote(60_000m);

        order.Status.Should().Be(OrderStatus.Rejected);
        order.RejectReason.Should().Contain(expected);
        _venue.Placed.Should().BeEmpty();
    }

    [Fact]
    public async Task WhatTheAccountHolds_IsWhatTheVenueSays_AndAFailedReadingChangesNothing()
    {
        (int fundId, int accountId) = await NewLiveAccountAsync();
        Quote(60_000m, InstrumentType.Perp);
        _venue.Account = new VenueAccountReport(1_500m, 2_400.25m, [new VenueHolding("BTC", InstrumentType.Spot, 0.5m, null, null, 59_990m), new VenueHolding("BTC", InstrumentType.Perp, -0.25m, 61_500m, 75_000m, 60_000m), new VenueHolding("KCS", InstrumentType.Spot, 100m, null, null, 10m)]);

        await ReadAccountAsync(accountId);
        TradingAccountDto read = await AccountAsync(fundId);
        // Sold elsewhere, and the venue is unreachable on the next reading.
        _venue.OnAccount = new VenueUnclearException("no answer in time");
        Clock.Now += TimeSpan.FromSeconds(6);
        await ReadAccountAsync(accountId);
        TradingAccountDto failed = await AccountAsync(fundId);
        _venue.OnAccount = null;
        _venue.Account = new VenueAccountReport(31_500m, 2_400.25m, [new VenueHolding("BTC", InstrumentType.Perp, -0.25m, 61_500m, 75_000m, 60_000m)]);
        await ReadAccountAsync(accountId);
        TradingAccountDto after = await AccountAsync(fundId);

        // Cash, the futures account, and the coins at their prices: 1,500 + 2,400.25 + 0.5 × 60,000 + 100 × 10.
        read.EquityUsd.Should().Be(34_900.25m);
        read.LiveReadError.Should().BeNull();
        read.Positions.Select(p => (p.Asset, p.Type, p.Quantity)).Should().BeEquivalentTo([("BTC", InstrumentType.Spot, 0.5m), ("BTC", InstrumentType.Perp, -0.25m), ("KCS", InstrumentType.Spot, 100m)]);
        TradingPositionDto perp = read.Positions.Single(p => p.Type == InstrumentType.Perp);
        perp.AveragePrice.Should().Be(61_500m);
        perp.LiquidationPrice.Should().Be(75_000m);
        perp.LiquidationDistance.Should().Be(0.25m);
        failed.LiveReadError.Should().Be("no answer in time");
        failed.Positions.Should().HaveCount(3);
        failed.EquityUsd.Should().Be(34_900.25m);
        after.LiveReadError.Should().BeNull();
        after.Positions.Should().ContainSingle().Which.Type.Should().Be(InstrumentType.Perp);
        after.EquityUsd.Should().Be(33_900.25m);
    }

    [Fact]
    public async Task TwoReadingsOfOneOrder_CannotBothBeSaved()
    {
        (_, int accountId) = await NewLiveAccountAsync();
        long id = await SendAsync(accountId, OrderSide.Buy, 0.1m, 59_900m);
        using IServiceScope one = _app!.Services.CreateScope();
        using IServiceScope two = _app!.Services.CreateScope();
        FtDbContext first = one.ServiceProvider.GetRequiredService<FtDbContext>();
        FtDbContext second = two.ServiceProvider.GetRequiredService<FtDbContext>();
        Order mine = await first.Orders.SingleAsync(o => o.Id == id);
        Order theirs = await second.Orders.SingleAsync(o => o.Id == id);

        mine.Status = OrderStatus.Accepted;
        await first.SaveChangesAsync();
        theirs.Status = OrderStatus.Accepted;
        Func<Task> late = () => second.SaveChangesAsync();

        // Two copies of the execution service that both read the order as new cannot both mark it as being sent.
        await late.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }
}
