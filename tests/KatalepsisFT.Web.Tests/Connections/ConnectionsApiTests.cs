using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using KatalepsisFT.Application.Auth;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Application.Holdings;
using KatalepsisFT.Application.Structure;
using KatalepsisFT.Domain.Access;
using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Domain.Funds;
using KatalepsisFT.Domain.Instruments;
using KatalepsisFT.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KatalepsisFT.Web.Tests.Connections;

/// <summary>A venue that answers whatever the test tells it to.</summary>
internal sealed class FakeVenue(Venue venue, bool needsPassphrase = false) : IVenueAdapter
{
    public CredentialCheck Check { get; set; } = new(false, false);
    public VenueException? Failure { get; set; }
    public Dictionary<string, VenueSnapshot> SnapshotsByKey { get; } = [];

    public Venue Venue => venue;

    public string DisplayName => venue.ToString();

    public bool NeedsPassphrase => needsPassphrase;

    public string WorkerGroup => "cefi";

    public Task<CredentialCheck> CheckCredentialsAsync(VenueCredentials credentials, CancellationToken cancellationToken) => Failure is null ? Task.FromResult(Check) : throw Failure;

    public Task<VenueSnapshot> GetSnapshotAsync(VenueCredentials credentials, CancellationToken cancellationToken) =>
        Failure is null ? Task.FromResult(SnapshotsByKey[credentials.ApiKey]) : throw Failure;
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
        connections.Venues.Select(v => v.Venue).Should().Equal(Venue.Okx, Venue.Deribit);
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
