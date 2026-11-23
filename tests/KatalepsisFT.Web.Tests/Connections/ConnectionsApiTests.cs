using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using KatalepsisFT.Application.Alerts;
using KatalepsisFT.Application.Auth;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Application.Holdings;
using KatalepsisFT.Application.Live;
using KatalepsisFT.Application.Structure;
using KatalepsisFT.Domain.Access;
using KatalepsisFT.Domain.Alerts;
using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Domain.Funds;
using KatalepsisFT.Domain.Instruments;
using KatalepsisFT.Domain.Positions;
using KatalepsisFT.Persistence;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KatalepsisFT.Web.Tests.Connections;

/// <summary>A venue that answers whatever the test tells it to.</summary>
internal sealed class FakeVenue(Venue venue, bool needsPassphrase = false, bool usesAddress = false) : IVenueAdapter
{
    public CredentialCheck Check { get; set; } = new(false, false);
    public VenueException? Failure { get; set; }
    public Dictionary<string, VenueSnapshot> SnapshotsByKey { get; } = [];

    public Venue Venue => venue;

    public string DisplayName => venue.ToString();

    public bool NeedsPassphrase => needsPassphrase;

    public bool UsesAddress => usesAddress;

    public string WorkerGroup => "cefi";

    public Task<CredentialCheck> CheckCredentialsAsync(VenueCredentials credentials, CancellationToken cancellationToken) => Failure is null ? Task.FromResult(Check) : throw Failure;

    public Task<VenueSnapshot> GetSnapshotAsync(VenueCredentials credentials, CancellationToken cancellationToken) =>
        Failure is null ? Task.FromResult(SnapshotsByKey[credentials.ApiKey]) : throw Failure;

    public Dictionary<string, List<VenueTransfer>> TransfersByKey { get; } = [];
    public VenueException? TransfersFailure { get; set; }

    public Task<IReadOnlyList<VenueTransfer>> GetTransfersAsync(VenueCredentials credentials, DateTimeOffset since, CancellationToken cancellationToken) =>
        TransfersFailure is null ? Task.FromResult<IReadOnlyList<VenueTransfer>>(TransfersByKey.GetValueOrDefault(credentials.ApiKey) ?? []) : throw TransfersFailure;
}

public class ConnectionsApiTests(TestDatabase database) : IClassFixture<TestDatabase>, IAsyncLifetime
{
    private const string Secret = "the-secret-that-must-never-come-back";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>Cash 1,000; 2 BTC at 50,000; short 10 ETH perp at 3,000 with 250 of profit not yet settled. NAV 101,250.</summary>
    private static readonly VenueSnapshot Book = new(
        DateTimeOffset.UtcNow,
        [new VenuePosition(InstrumentType.Cash, "USDC", 1_000m, 1m), new VenuePosition(InstrumentType.Spot, "BTC", 2m, 50_000m), new VenuePosition(InstrumentType.Perp, "ETH", -10m, 3_000m, 3_100m, 250m)],
        101_300m
    );

    private static readonly FakeVenue Deribit = new(Venue.Deribit);
    private static readonly FakeVenue Okx = new(Venue.Okx, needsPassphrase: true);
    private static readonly FakeVenue Wallet = new(Venue.Hyperliquid, usesAddress: true);
    private static WebApplicationFactory<Program>? _app;

    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        _app ??= database.CreateApp(services: services =>
        {
            foreach (ServiceDescriptor descriptor in services.Where(s => s.ServiceType == typeof(IVenueAdapter)).ToList())
            {
                services.Remove(descriptor);
            }

            services.AddSingleton<IVenueAdapter>(Deribit);
            services.AddSingleton<IVenueAdapter>(Okx);
            services.AddSingleton<IVenueAdapter>(Wallet);
        });
        Deribit.Check = new CredentialCheck(false, false);
        Deribit.Failure = null;
        _admin = await SignInAsync(Roles.Admin);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<HttpClient> SignInAsync(string role)
    {
        string email = await database.AddUserAsync(_app!, role);
        HttpClient client = TestDatabase.CreateClient(_app!);
        await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, TestDatabase.Password, false));
        return client;
    }

    private async Task<T> Get<T>(string url) => (await _admin.GetFromJsonAsync<T>(url, Json))!;

    /// <summary>A new fund with one sleeve and one manager; returns the fund and the manager.</summary>
    private async Task<(int FundId, int ManagerId)> NewFundAsync()
    {
        NewFundRequest request = new($"Fund {Guid.NewGuid():N}", null, [new NewSleeveRequest("Directional", MandateType.Directional, [new NewManagerRequest("Alpha Desk", 1_000_000m)])]);
        HttpResponseMessage response = await _admin.PostAsJsonAsync("/api/funds", request, Json);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        int fundId = (await response.Content.ReadFromJsonAsync<FundCreatedDto>(Json))!.Id;
        return (fundId, (await Get<ConnectionsDto>($"/api/funds/{fundId}/connections")).Managers.Single().Id);
    }

    private Task<HttpResponseMessage> AddAsync(int managerId, string key, Venue venue = Venue.Deribit, string? passphrase = null, VenueSnapshot? book = null)
    {
        (venue == Venue.Deribit ? Deribit : Okx).SnapshotsByKey[key] = book ?? Book;
        return _admin.PostAsJsonAsync("/api/connections", new AddConnectionRequest(managerId, venue, "Main account", key, Secret, passphrase), Json);
    }

    [Fact]
    public async Task NewFund_IsListed_WithManagersThatReadFromVenues()
    {
        (int fundId, int managerId) = await NewFundAsync();

        ConnectionsDto connections = await Get<ConnectionsDto>($"/api/funds/{fundId}/connections");
        FundOverviewDto overview = await Get<FundOverviewDto>($"/api/funds/{fundId}/overview");

        (await Get<List<FundListItemDto>>("/api/funds")).Should().Contain(f => f.Id == fundId);
        connections.CanAddKeys.Should().BeTrue();
        connections.Venues.Select(v => v.Venue).Should().Equal(Venue.Okx, Venue.Deribit, Venue.Hyperliquid);
        connections.Venues.Single(v => v.Venue == Venue.Hyperliquid).UsesAddress.Should().BeTrue();
        connections.Managers.Should().ContainSingle(m => m.Id == managerId && m.Name == "Alpha Desk" && m.Sleeve == "Directional");
        connections.Connections.Should().BeEmpty();
        overview.Fund.IsDemo.Should().BeFalse();
        overview.Totals.NavUsd.Should().Be(0m);
    }

    [Fact]
    public async Task NewFund_WithoutSleeves_OrWithATakenName_IsRefused()
    {
        NewFundRequest valid = new($"Fund {Guid.NewGuid():N}", null, [new NewSleeveRequest("Beta", MandateType.Beta, [new NewManagerRequest("One", 0m)])]);
        await _admin.PostAsJsonAsync("/api/funds", valid, Json);

        (await _admin.PostAsJsonAsync("/api/funds", valid, Json)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _admin.PostAsJsonAsync("/api/funds", valid with { Name = "No sleeves", Sleeves = [] }, Json)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _admin.PostAsJsonAsync("/api/funds", valid with { Name = "No managers", Sleeves = [new NewSleeveRequest("Beta", MandateType.Beta, [])] }, Json)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ReadOnlyKey_IsStoredEncrypted_AndTheBookShowsOnTheFund()
    {
        (int fundId, int managerId) = await NewFundAsync();

        HttpResponseMessage response = await AddAsync(managerId, "key-read-only-WXYZ");
        string body = await response.Content.ReadAsStringAsync();
        ConnectionDto connection = JsonSerializer.Deserialize<ConnectionDto>(body, Json)!;
        FundOverviewDto overview = await Get<FundOverviewDto>($"/api/funds/{fundId}/overview");

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        connection.State.Should().Be(ConnectionState.Live);
        connection.KeyHint.Should().Be("WXYZ");
        connection.PermissionsVerified.Should().BeTrue();
        connection.Positions.Should().Be(3);
        connection.ComputedNavUsd.Should().Be(101_250m);
        connection.ReportedEquityUsd.Should().Be(101_300m);
        body.Should().NotContain(Secret).And.NotContain("key-read-only");
        (await _admin.GetStringAsync($"/api/funds/{fundId}/connections")).Should().NotContain(Secret).And.NotContain("key-read-only");
        overview.Totals.NavUsd.Should().Be(101_250m);
        overview.Totals.LongUsd.Should().Be(100_000m);
        overview.Totals.ShortUsd.Should().Be(-30_000m);
        overview.Totals.CashUsd.Should().Be(1_000m);

        using IServiceScope scope = _app!.Services.CreateScope();
        string stored = await scope.ServiceProvider.GetRequiredService<FtDbContext>().VenueConnections.Where(c => c.Id == connection.Id).Select(c => c.EncryptedCredentials).SingleAsync();
        stored.Should().StartWith("v1.").And.NotContain(Secret);
    }

    [Theory]
    [InlineData(true, false, "can trade")]
    [InlineData(false, true, "can withdraw")]
    [InlineData(true, true, "can trade and withdraw")]
    public async Task KeyThatCanTradeOrWithdraw_IsRefused_AndNothingIsStored(bool canTrade, bool canWithdraw, string expected)
    {
        (int fundId, int managerId) = await NewFundAsync();
        Deribit.Check = new CredentialCheck(canTrade, canWithdraw);

        HttpResponseMessage response = await AddAsync(managerId, "key-too-powerful");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<ErrorResponse>(Json))!.Message.Should().Contain(expected);
        (await Get<ConnectionsDto>($"/api/funds/{fundId}/connections")).Connections.Should().BeEmpty();
    }

    [Fact]
    public async Task KeyTheVenueCannotDescribe_IsAccepted_ButMarkedUnverified()
    {
        (_, int managerId) = await NewFundAsync();
        Deribit.Check = new CredentialCheck(null, null);

        ConnectionDto connection = (await (await AddAsync(managerId, "key-unknown-permissions")).Content.ReadFromJsonAsync<ConnectionDto>(Json))!;

        connection.PermissionsVerified.Should().BeFalse();
    }

    [Fact]
    public async Task KeyTheVenueRefuses_OrAnUnreachableVenue_IsReportedInWords()
    {
        (_, int managerId) = await NewFundAsync();

        Deribit.Failure = new VenueException(VenueFailure.Rejected, "invalid_credentials (code 13004) on public/auth.");
        HttpResponseMessage rejected = await AddAsync(managerId, "key-wrong");
        Deribit.Failure = new VenueException(VenueFailure.Unavailable, "Deribit did not answer public/auth.");
        HttpResponseMessage unreachable = await AddAsync(managerId, "key-wrong");

        rejected.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await rejected.Content.ReadFromJsonAsync<ErrorResponse>(Json))!.Message.Should().StartWith("Deribit refused the key: invalid_credentials");
        (await unreachable.Content.ReadFromJsonAsync<ErrorResponse>(Json))!.Message.Should().StartWith("Deribit could not be reached");
    }

    [Fact]
    public async Task VenueThatNeedsAPassphrase_RefusesAKeyWithoutOne()
    {
        (_, int managerId) = await NewFundAsync();

        (await AddAsync(managerId, "okx-key", Venue.Okx)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await AddAsync(managerId, "okx-key", Venue.Okx, "passphrase")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task FailedRead_KeepsTheLastBook_AndSaysWhy()
    {
        (int fundId, int managerId) = await NewFundAsync();
        ConnectionDto connection = (await (await AddAsync(managerId, "key-that-will-fail")).Content.ReadFromJsonAsync<ConnectionDto>(Json))!;

        Deribit.Failure = new VenueException(VenueFailure.Unavailable, "Deribit did not answer private/get_positions.");
        HttpResponseMessage response = await _admin.PostAsJsonAsync($"/api/connections/{connection.Id}/sync", new { });
        ConnectionDto after = (await response.Content.ReadFromJsonAsync<ConnectionDto>(Json))!;

        after.State.Should().Be(ConnectionState.Failed);
        after.LastError.Should().Be("Deribit did not answer private/get_positions.");
        after.LastSuccessAt.Should().BeCloseTo(connection.LastSuccessAt!.Value, TimeSpan.FromMilliseconds(1));
        after.Positions.Should().Be(3);
        (await Get<FundOverviewDto>($"/api/funds/{fundId}/overview")).Totals.NavUsd.Should().Be(101_250m);
    }

    [Fact]
    public async Task NewRead_ReplacesTodaysBook()
    {
        (int fundId, int managerId) = await NewFundAsync();
        ConnectionDto connection = (await (await AddAsync(managerId, "key-that-changes")).Content.ReadFromJsonAsync<ConnectionDto>(Json))!;

        Deribit.SnapshotsByKey["key-that-changes"] = new VenueSnapshot(DateTimeOffset.UtcNow, [new VenuePosition(InstrumentType.Spot, "BTC", 1m, 60_000m)]);
        await _admin.PostAsJsonAsync($"/api/connections/{connection.Id}/sync", new { });
        TotalsDto totals = (await Get<FundOverviewDto>($"/api/funds/{fundId}/overview")).Totals;

        totals.NavUsd.Should().Be(60_000m);
        totals.ShortUsd.Should().Be(0m);
        totals.CashUsd.Should().Be(0m);
    }

    [Fact]
    public async Task TwoConnectionsOfOneManager_AddUp_AndSwitchingOneOffRemovesItsPositions()
    {
        (int fundId, int managerId) = await NewFundAsync();
        await AddAsync(managerId, "key-first");
        VenueSnapshot second = new(DateTimeOffset.UtcNow, [new VenuePosition(InstrumentType.Spot, "BTC", 1m, 50_000m), new VenuePosition(InstrumentType.Perp, "ETH", 4m, 3_000m, 2_900m, 400m)]);
        ConnectionDto okx = (await (await AddAsync(managerId, "key-second", Venue.Okx, "passphrase", second)).Content.ReadFromJsonAsync<ConnectionDto>(Json))!;

        TotalsDto both = (await Get<FundOverviewDto>($"/api/funds/{fundId}/overview")).Totals;
        ManagerDetailDto manager = await Get<ManagerDetailDto>($"/api/managers/{managerId}");
        await _admin.PostAsJsonAsync($"/api/connections/{okx.Id}/enabled", new { enabled = false });
        TotalsDto one = (await Get<FundOverviewDto>($"/api/funds/{fundId}/overview")).Totals;
        (await _admin.DeleteAsync($"/api/connections/{okx.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        // 101,250 + 50,000 + 400; the two ETH perp legs net to −6 ETH.
        both.NavUsd.Should().Be(151_650m);
        both.LongUsd.Should().Be(150_000m);
        both.ShortUsd.Should().Be(-18_000m);
        manager.Positions.Should().HaveCount(3);
        one.NavUsd.Should().Be(101_250m);
        (await Get<ConnectionsDto>($"/api/funds/{fundId}/connections")).Connections.Should().ContainSingle();
    }

    [Fact]
    public async Task ConnectionJoiningOrLeavingTheBook_IsMoneyMoving_NotPerformance()
    {
        (_, int managerId) = await NewFundAsync();
        await AddAsync(managerId, "key-stays");
        VenueSnapshot second = new(DateTimeOffset.UtcNow, [new VenuePosition(InstrumentType.Spot, "BTC", 1m, 50_000m)]);
        ConnectionDto okx = (await (await AddAsync(managerId, "key-comes-and-goes", Venue.Okx, "passphrase", second)).Content.ReadFromJsonAsync<ConnectionDto>(Json))!;
        await _admin.PostAsJsonAsync($"/api/connections/{okx.Id}/enabled", new { enabled = false });
        // While it was off the account grew; switching it on brings in what is there now, not what was last read.
        Okx.SnapshotsByKey["key-comes-and-goes"] = new VenueSnapshot(DateTimeOffset.UtcNow, [new VenuePosition(InstrumentType.Spot, "BTC", 1m, 60_000m)]);
        await _admin.PostAsJsonAsync($"/api/connections/{okx.Id}/enabled", new { enabled = true });
        (await _admin.DeleteAsync($"/api/connections/{okx.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        using IServiceScope read = _app!.Services.CreateScope();
        FtDbContext db = read.ServiceProvider.GetRequiredService<FtDbContext>();
        List<decimal> flows = await db.CashFlows.Where(f => f.ManagerAccountId == managerId).OrderBy(f => f.Id).Select(f => f.AmountUsd).ToListAsync();
        flows.Should().Equal(101_250m, 50_000m, -50_000m, 60_000m, -60_000m);
        (await db.NavSnapshots.Where(n => n.ManagerAccountId == managerId).Select(n => n.NavUsd).SingleAsync()).Should().Be(101_250m);
    }

    [Fact]
    public async Task ManagerNotReadForDays_StaysInTheFundAtItsLastReading_AndDoesNotHoldTheOthersBack()
    {
        NewFundRequest request = new($"Fund {Guid.NewGuid():N}", null, [new NewSleeveRequest("Directional", MandateType.Directional, [new NewManagerRequest("Stale Desk", 1m), new NewManagerRequest("Fresh Desk", 1m)])]);
        int fundId = (await (await _admin.PostAsJsonAsync("/api/funds", request, Json)).Content.ReadFromJsonAsync<FundCreatedDto>(Json))!.Id;
        List<ConnectionManagerDto> managers = (await Get<ConnectionsDto>($"/api/funds/{fundId}/connections")).Managers.ToList();
        int stale = managers.Single(m => m.Name == "Stale Desk").Id;
        int fresh = managers.Single(m => m.Name == "Fresh Desk").Id;
        await AddAsync(stale, "key-stale");
        await AddAsync(fresh, "key-fresh", Venue.Okx, "passphrase", new VenueSnapshot(DateTimeOffset.UtcNow, [new VenuePosition(InstrumentType.Spot, "BTC", 1m, 50_000m)]));
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        using (IServiceScope scope = _app!.Services.CreateScope())
        {
            // The stale desk was last read five days ago; the fresh one started three days ago and was read today.
            FtDbContext db = scope.ServiceProvider.GetRequiredService<FtDbContext>();
            await db.NavSnapshots.Where(n => n.ManagerAccountId == stale).ExecuteUpdateAsync(u => u.SetProperty(n => n.Date, today.AddDays(-5)));
            await db.PositionSnapshots.Where(n => n.ManagerAccountId == stale).ExecuteUpdateAsync(u => u.SetProperty(n => n.Date, today.AddDays(-5)));
            await db.CashFlows.Where(n => n.ManagerAccountId == stale || n.ManagerAccountId == fresh).ExecuteDeleteAsync();
            db.NavSnapshots.Add(new NavSnapshot { ManagerAccountId = fresh, Date = today.AddDays(-3), NavUsd = 50_000m });
            await db.SaveChangesAsync();
        }

        FundOverviewDto overview = await Get<FundOverviewDto>($"/api/funds/{fundId}/overview");
        using IServiceScope read = _app!.Services.CreateScope();
        KatalepsisFT.Application.Performance.NavHistory history = await KatalepsisFT.Application.Performance.NavHistory.LoadAsync(read.ServiceProvider.GetRequiredService<FtDbContext>(), [stale, fresh], CancellationToken.None);
        KatalepsisFT.Application.Performance.NavSeries series = history.Series([stale, fresh], history.AsOf([stale, fresh]));

        overview.AsOf.Should().Be(today);
        overview.Totals.NavUsd.Should().Be(151_250m);
        // Five days ago the stale desk alone; the fresh desk arrives two days later as a flow, never as a gain.
        series.Dates[^1].Should().Be(today);
        series.Navs.Should().Equal(101_250m, 101_250m, 151_250m, 151_250m, 151_250m, 151_250m);
        series.Returns().Should().OnlyContain(r => r == 0d);
    }

    [Fact]
    public async Task ReadsOfOneManagerAtTheSameMoment_AllSucceed()
    {
        (int fundId, int managerId) = await NewFundAsync();
        ConnectionDto first = (await (await AddAsync(managerId, "key-racing-one")).Content.ReadFromJsonAsync<ConnectionDto>(Json))!;
        VenueSnapshot other = new(DateTimeOffset.UtcNow, [new VenuePosition(InstrumentType.Spot, "BTC", 1m, 50_000m)]);
        ConnectionDto second = (await (await AddAsync(managerId, "key-racing-two", Venue.Okx, "passphrase", other)).Content.ReadFromJsonAsync<ConnectionDto>(Json))!;

        HttpResponseMessage[] answers = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => _admin.PostAsJsonAsync($"/api/connections/{(i % 2 == 0 ? first.Id : second.Id)}/sync", new { })));

        answers.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);
        (await Get<ConnectionsDto>($"/api/funds/{fundId}/connections")).Connections.Should().OnlyContain(c => c.State == ConnectionState.Live);
        (await Get<FundOverviewDto>($"/api/funds/{fundId}/overview")).Totals.NavUsd.Should().Be(151_250m);
    }

    [Fact]
    public async Task SameKeyTwice_IsRefused()
    {
        (_, int managerId) = await NewFundAsync();
        await AddAsync(managerId, "key-only-once");

        HttpResponseMessage again = await AddAsync(managerId, "key-only-once");

        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await again.Content.ReadFromJsonAsync<ErrorResponse>(Json))!.Message.Should().Be("This key is already connected in this fund as \"Main account\".");
    }

    [Fact]
    public async Task BalanceOneVenueCallsCash_AndAnotherCallsSpot_IsOneLine()
    {
        (int fundId, int managerId) = await NewFundAsync();
        await AddAsync(managerId, "key-cash", book: new VenueSnapshot(DateTimeOffset.UtcNow, [new VenuePosition(InstrumentType.Cash, "USDT", 1_000m, 1m)]));
        VenueSnapshot spot = new(DateTimeOffset.UtcNow, [new VenuePosition(InstrumentType.Spot, "USDT", 500m, 1m)]);

        HttpResponseMessage added = await AddAsync(managerId, "key-spot", Venue.Okx, "passphrase", spot);

        added.StatusCode.Should().Be(HttpStatusCode.OK, await added.Content.ReadAsStringAsync());
        (await Get<FundOverviewDto>($"/api/funds/{fundId}/overview")).Totals.NavUsd.Should().Be(1_500m);
        (await Get<ManagerDetailDto>($"/api/managers/{managerId}")).Positions.Should().ContainSingle().Which.Quantity.Should().Be(1_500m);
    }

    [Fact]
    public async Task ReadThatFailsInAnUnexpectedWay_ShowsOnTheConnection()
    {
        (int fundId, int managerId) = await NewFundAsync();
        ConnectionDto connection = (await (await AddAsync(managerId, "key-then-broken")).Content.ReadFromJsonAsync<ConnectionDto>(Json))!;
        Deribit.SnapshotsByKey.Remove("key-then-broken");

        await _admin.PostAsJsonAsync($"/api/connections/{connection.Id}/sync", new { });

        ConnectionDto after = (await Get<ConnectionsDto>($"/api/funds/{fundId}/connections")).Connections.Single();
        after.State.Should().Be(ConnectionState.Failed);
        after.LastError.Should().Be("Unexpected error: KeyNotFoundException. The server log has the details.");
    }

    [Fact]
    public async Task EachRead_ChecksTheFundsAlertRules()
    {
        (int fundId, int managerId) = await NewFundAsync();
        SaveAlertRuleRequest rule = new(AlertScopeKind.Fund, fundId, AlertMetric.NetExposureOverNav, AlertOperator.Above, 0.5m, AlertSeverity.Warning, true);
        await _admin.PostAsJsonAsync($"/api/funds/{fundId}/alert-rules", rule, Json);
        (await Get<AlertListDto>($"/api/funds/{fundId}/alerts")).Alerts.Should().BeEmpty();

        // Net exposure 70,000 on a NAV of 101,250 is 69%.
        ConnectionDto connection = (await (await AddAsync(managerId, "key-with-alert")).Content.ReadFromJsonAsync<ConnectionDto>(Json))!;
        AlertDto raised = (await Get<AlertListDto>($"/api/funds/{fundId}/alerts")).Alerts.Single();
        Deribit.SnapshotsByKey["key-with-alert"] = new VenueSnapshot(DateTimeOffset.UtcNow, [new VenuePosition(InstrumentType.Cash, "USDC", 1_000m, 1m)]);
        await _admin.PostAsJsonAsync($"/api/connections/{connection.Id}/sync", new { });

        raised.Value.Should().BeApproximately(70_000m / 101_250m, 0.000001m);
        (await Get<AlertListDto>($"/api/funds/{fundId}/alerts")).Alerts.Should().BeEmpty();
        (await Get<AlertListDto>($"/api/funds/{fundId}/alerts?state=Resolved")).Alerts.Single().Actions[^1].Kind.Should().Be(AlertActionKind.Recovered);
    }

    [Fact]
    public async Task DepositsAndWithdrawals_BecomeCashFlows_Once()
    {
        (_, int managerId) = await NewFundAsync();
        DateTimeOffset soon = DateTimeOffset.UtcNow.AddMinutes(1);
        Deribit.TransfersByKey["key-with-transfers"] =
        [
            new VenueTransfer("dep:1", soon, "USDC", 5_000m),
            // Valued at the 50,000 the account's own BTC is marked at.
            new VenueTransfer("wd:7", soon, "btc", -0.1m),
            new VenueTransfer("dep:old", DateTimeOffset.UtcNow.AddDays(-3), "USDC", 9_999m),
        ];

        ConnectionDto connection = (await (await AddAsync(managerId, "key-with-transfers")).Content.ReadFromJsonAsync<ConnectionDto>(Json))!;
        using (IServiceScope scope = _app!.Services.CreateScope())
        {
            // Make the next read look for transfers again instead of waiting for the interval.
            FtDbContext db = scope.ServiceProvider.GetRequiredService<FtDbContext>();
            await db.VenueConnections.Where(c => c.Id == connection.Id).ExecuteUpdateAsync(u => u.SetProperty(c => c.TransfersCheckedAt, DateTimeOffset.UtcNow.AddHours(-1)));
        }

        await _admin.PostAsJsonAsync($"/api/connections/{connection.Id}/sync", new { });

        using IServiceScope read = _app!.Services.CreateScope();
        // The connection's own arrival in the book is a flow too; it has no transfer behind it.
        var flows = await read.ServiceProvider.GetRequiredService<FtDbContext>().CashFlows.Where(f => f.ManagerAccountId == managerId && f.ExternalId != null).OrderBy(f => f.AmountUsd).ToListAsync();
        flows.Select(f => f.AmountUsd).Should().Equal(-5_000m, 5_000m);
        flows.Select(f => f.Kind).Should().Equal(CashFlowKind.Redemption, CashFlowKind.Subscription);
        flows.Should().OnlyContain(f => f.Date == DateOnly.FromDateTime(DateTime.UtcNow) || f.Date == DateOnly.FromDateTime(soon.UtcDateTime));
        flows[0].Note.Should().Be("Deribit withdrawal of 0.1 BTC");
        connection.TransfersError.Should().BeNull();
    }

    [Fact]
    public async Task TransfersThatCannotBeRead_AreReported_ButPositionsStillArrive()
    {
        (int fundId, int managerId) = await NewFundAsync();
        Deribit.TransfersFailure = new VenueException(VenueFailure.Rejected, "the key lacks the wallet:read scope.");
        try
        {
            ConnectionDto connection = (await (await AddAsync(managerId, "key-without-wallet-scope")).Content.ReadFromJsonAsync<ConnectionDto>(Json))!;

            connection.State.Should().Be(ConnectionState.Live);
            connection.TransfersError.Should().Be("the key lacks the wallet:read scope.");
            (await Get<FundOverviewDto>($"/api/funds/{fundId}/overview")).Totals.NavUsd.Should().Be(101_250m);
        }
        finally
        {
            Deribit.TransfersFailure = null;
        }
    }

    [Fact]
    public async Task EachRead_KeepsAnIntradaySample_ButNotMoreThanOnePerStep()
    {
        (int fundId, int managerId) = await NewFundAsync();
        ConnectionDto connection = (await (await AddAsync(managerId, "key-with-samples")).Content.ReadFromJsonAsync<ConnectionDto>(Json))!;
        await _admin.PostAsJsonAsync($"/api/connections/{connection.Id}/sync", new { });

        IntradayDto intraday = await Get<IntradayDto>($"/api/funds/{fundId}/intraday");

        using IServiceScope scope = _app!.Services.CreateScope();
        FtDbContext db = scope.ServiceProvider.GetRequiredService<FtDbContext>();
        (await db.NavSamples.CountAsync(s => s.ManagerAccountId == managerId)).Should().Be(1);
        intraday.Times.Should().NotBeEmpty();
        intraday.NavUsd.Should().OnlyContain(v => v == 101_250m);
        intraday.GrossUsd.Should().OnlyContain(v => v == 130_000m);
        intraday.NetUsd.Should().OnlyContain(v => v == 70_000m);
        intraday.Times.Should().OnlyContain(t => t.Minute % 5 == 0 && t.Second == 0);
    }

    [Fact]
    public async Task Intraday_AddsManagersUp_CarryingEachOnesLatestSampleForward()
    {
        (int fundId, int managerId) = await NewFundAsync();
        int sleeveId;
        using (IServiceScope scope = _app!.Services.CreateScope())
        {
            FtDbContext db = scope.ServiceProvider.GetRequiredService<FtDbContext>();
            sleeveId = await db.ManagerAccounts.Where(m => m.Id == managerId).Select(m => m.SleeveId).SingleAsync();
            ManagerAccount second = new() { SleeveId = sleeveId, Name = "Second", DataSource = ManagerDataSource.Venue };
            db.ManagerAccounts.Add(second);
            await db.SaveChangesAsync();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            db.NavSamples.AddRange(
                new NavSample { ManagerAccountId = managerId, At = now.AddMinutes(-62), NavUsd = 100m, GrossUsd = 50m, NetUsd = 50m },
                new NavSample { ManagerAccountId = second.Id, At = now.AddMinutes(-31), NavUsd = 40m, GrossUsd = 40m, NetUsd = -40m },
                new NavSample { ManagerAccountId = managerId, At = now.AddMinutes(-12), NavUsd = 110m, GrossUsd = 60m, NetUsd = 60m }
            );
            await db.SaveChangesAsync();
        }

        IntradayDto intraday = await Get<IntradayDto>($"/api/funds/{fundId}/intraday?hours=2");

        intraday.NavUsd[0].Should().Be(100m);
        intraday.NavUsd.Should().ContainInOrder(100m, 140m, 150m);
        intraday.NavUsd[^1].Should().Be(150m);
        intraday.NetUsd[^1].Should().Be(20m);
        intraday.Times.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        intraday.Times.Zip(intraday.Times.Skip(1), (a, b) => b - a).Should().OnlyContain(gap => gap == TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task Intraday_OfAFundNeverRead_IsEmpty_AndHoursAreChecked()
    {
        (int fundId, _) = await NewFundAsync();

        (await Get<IntradayDto>($"/api/funds/{fundId}/intraday")).Times.Should().BeEmpty();
        (await _admin.GetAsync($"/api/funds/{fundId}/intraday?hours=0")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _admin.GetAsync($"/api/funds/{fundId}/intraday?hours=9999")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _admin.GetAsync("/api/funds/999999/intraday")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task BrowserWatchingAFund_IsToldWhenItChanges_AndOnlyThatFund()
    {
        (int fundId, int managerId) = await NewFundAsync();
        (_, int otherManagerId) = await NewFundAsync();
        string email = await database.AddUserAsync(_app!, Roles.Viewer);
        HttpClient http = TestDatabase.CreateClient(_app!);
        HttpResponseMessage login = await http.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, TestDatabase.Password, false));
        string cookie = login.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        List<int> heard = [];
        await using HubConnection live = new HubConnectionBuilder()
            .WithUrl(
                "https://localhost/hubs/live",
                options =>
                {
                    // The test server has no real sockets; the browser uses WebSockets only.
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => _app!.Server.CreateHandler();
                    options.Headers["Cookie"] = cookie;
                }
            )
            .Build();
        live.On<int>("fundChanged", heard.Add);
        await live.StartAsync();
        await live.InvokeAsync("WatchFund", fundId);

        await AddAsync(otherManagerId, "key-of-another-fund");
        await AddAsync(managerId, "key-of-the-watched-fund");
        for (int i = 0; i < 50 && heard.Count == 0; i++)
        {
            await Task.Delay(100);
        }

        heard.Should().Equal(fundId);
    }

    [Fact]
    public async Task LiveSocket_NeedsASignedInUser()
    {
        HttpResponseMessage response = await TestDatabase.CreateClient(_app!).PostAsync("/hubs/live/negotiate?negotiateVersion=1", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task WalletAddress_IsAddedWithoutASecret_ButAKeyVenueStillNeedsOne()
    {
        (int fundId, int managerId) = await NewFundAsync();
        const string address = "0x00000000000000000000000000000000000000aa";
        Wallet.SnapshotsByKey[address] = Book;

        HttpResponseMessage wallet = await _admin.PostAsJsonAsync("/api/connections", new AddConnectionRequest(managerId, Venue.Hyperliquid, "Main wallet", address, null, null), Json);
        HttpResponseMessage keyWithoutSecret = await _admin.PostAsJsonAsync("/api/connections", new AddConnectionRequest(managerId, Venue.Deribit, "x", "key-no-secret", " ", null), Json);
        ConnectionDto connection = (await wallet.Content.ReadFromJsonAsync<ConnectionDto>(Json))!;

        wallet.StatusCode.Should().Be(HttpStatusCode.OK);
        connection.State.Should().Be(ConnectionState.Live);
        connection.KeyHint.Should().Be("00aa");
        connection.PermissionsVerified.Should().BeTrue();
        (await Get<FundOverviewDto>($"/api/funds/{fundId}/overview")).Totals.NavUsd.Should().Be(101_250m);
        keyWithoutSecret.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await keyWithoutSecret.Content.ReadFromJsonAsync<ErrorResponse>(Json))!.Message.Should().Be("Deribit keys come with a secret.");
    }

    [Fact]
    public async Task Viewer_CannotSeeOrChangeConnections()
    {
        (int fundId, int managerId) = await NewFundAsync();
        HttpClient viewer = await SignInAsync(Roles.Viewer);

        (await viewer.GetAsync($"/api/funds/{fundId}/connections")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.PostAsJsonAsync("/api/connections", new AddConnectionRequest(managerId, Venue.Deribit, "x", "k", "s", null), Json)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.PostAsJsonAsync("/api/funds", new NewFundRequest("Viewer fund", null, []), Json)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _admin.GetAsync("/api/funds/999999/connections")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _admin.DeleteAsync("/api/connections/999999")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(3, 4)]
    [InlineData(5, 15)]
    [InlineData(50, 15)]
    public void FailingConnection_IsTriedLessOften_UpToACap(int failures, int minutes)
    {
        ConnectionSyncService.Backoff(failures).Should().Be(TimeSpan.FromMinutes(minutes));
    }
}
