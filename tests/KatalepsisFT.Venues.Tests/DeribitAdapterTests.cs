using System.Net;
using System.Text;
using FluentAssertions;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Domain.Instruments;
using KatalepsisFT.Venues.Deribit;

namespace KatalepsisFT.Venues.Tests;

/// <summary>
/// Deribit's answers here are written by hand in the shape its API documentation gives; they are not recordings of a
/// real account. They pin down how each field is read, and must be confirmed against a real read-only key.
/// </summary>
public class DeribitAdapterTests
{
    private static readonly VenueCredentials Credentials = new("client-id", "client-secret");

    private const string Auth = """{"jsonrpc":"2.0","result":{"access_token":"token-1","expires_in":900,"scope":"account:read trade:read wallet:none block_trade:none session:x","token_type":"bearer"}}""";

    private const string Summaries = """
        {"jsonrpc":"2.0","result":{"summaries":[
          {"currency":"BTC","balance":2.5,"equity":2.62},
          {"currency":"USDC","balance":40000.0,"equity":40150.0},
          {"currency":"ETH","balance":0.0,"equity":0.0}
        ]}}
        """;

    private const string Positions = """
        {"jsonrpc":"2.0","result":[
          {"instrument_name":"BTC-PERPETUAL","kind":"future","direction":"sell","size":-100000.0,"size_currency":-1.25,"average_price":82000.0,"mark_price":80000.0,"index_price":80010.0,"floating_profit_loss":0.02,"total_profit_loss":0.031,"delta":-1.25},
          {"instrument_name":"SOL_USDC-PERPETUAL","kind":"future","direction":"buy","size":300.0,"size_currency":300.0,"average_price":150.0,"mark_price":151.5,"index_price":151.4,"floating_profit_loss":150.0,"delta":300.0},
          {"instrument_name":"BTC-25DEC26","kind":"future","direction":"buy","size":40000.0,"size_currency":0.5,"average_price":79000.0,"mark_price":81000.0,"index_price":80010.0,"floating_profit_loss":0.01,"delta":0.5},
          {"instrument_name":"BTC-25DEC26-90000-C","kind":"option","direction":"sell","size":-2.0,"average_price":0.05,"mark_price":0.04,"index_price":80000.0,"delta":-0.7,"floating_profit_loss":0.02},
          {"instrument_name":"ETH-PERPETUAL","kind":"future","direction":"zero","size":0.0,"size_currency":0.0,"average_price":0.0,"mark_price":3000.0,"index_price":3000.0,"floating_profit_loss":0.0,"delta":0.0}
        ]}
        """;

    private const string BtcIndex = """{"jsonrpc":"2.0","result":{"index_price":80000.0,"estimated_delivery_price":80000.0}}""";

    private sealed class StubHandler(Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            (HttpStatusCode status, string body) = respond(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static (DeribitAdapter Adapter, StubHandler Handler) Adapter(Func<string, (HttpStatusCode, string)?>? overrides = null)
    {
        StubHandler handler = new(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (overrides?.Invoke(path) is { } custom)
            {
                return custom;
            }

            return path switch
            {
                "/api/v2/public/auth" => (HttpStatusCode.OK, Auth),
                "/api/v2/private/get_account_summaries" => (HttpStatusCode.OK, Summaries),
                "/api/v2/private/get_positions" => (HttpStatusCode.OK, Positions),
                "/api/v2/public/get_index_price" => (HttpStatusCode.OK, BtcIndex),
                _ => (HttpStatusCode.NotFound, "{}"),
            };
        });
        return (new DeribitAdapter(new HttpClient(handler), TimeProvider.System), handler);
    }

    [Fact]
    public async Task Snapshot_ReadsBalancesAndEveryKindOfPosition_InCoinsAndUsd()
    {
        (DeribitAdapter adapter, StubHandler handler) = Adapter();

        VenueSnapshot snapshot = await adapter.GetSnapshotAsync(Credentials, CancellationToken.None);

        snapshot.Positions.Should().BeEquivalentTo(
            [
                new VenuePosition(InstrumentType.Spot, "BTC", 2.5m, 80_000m),
                new VenuePosition(InstrumentType.Cash, "USDC", 40_000m, 1m),
                // Inverse perp: 100,000 USD short is 1.25 BTC short; 0.02 BTC of open profit at the 80,010 index.
                new VenuePosition(InstrumentType.Perp, "BTC", -1.25m, 80_000m, 82_000m, 0.02m * 80_010m),
                // Linear perp: sizes and profit are already in SOL and USDC.
                new VenuePosition(InstrumentType.Perp, "SOL", 300m, 151.5m, 150m, 150m),
                new VenuePosition(InstrumentType.Future, "BTC", 0.5m, 81_000m, 79_000m, 0.01m * 80_010m, new DateOnly(2026, 12, 25)),
                // Option premium 0.04 BTC is 3,200 USD; a position delta of −0.7 on −2 contracts is 0.35 each.
                new VenuePosition(InstrumentType.Option, "BTC", -2m, 3_200m, 4_000m, null, new DateOnly(2026, 12, 25), 90_000m, OptionRight.Call, 80_000m, 0.35m),
            ]
        );
        snapshot.ReportedEquityUsd.Should().Be(2.62m * 80_000m + 40_150m);
        handler.Requests.Where(r => r.RequestUri!.AbsolutePath.Contains("/private/")).Should().OnlyContain(r => r.Headers.Authorization!.Scheme == "Bearer" && r.Headers.Authorization.Parameter == "token-1");
        handler.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
    }

    [Theory]
    [InlineData("account:read trade:read wallet:none", false, false)]
    [InlineData("account:read trade:read_write wallet:read", true, false)]
    [InlineData("account:read_write trade:read wallet:read_write", false, true)]
    [InlineData("account:read trade:read block_trade:read_write wallet:read_write", true, true)]
    public async Task CheckCredentials_ReadsWhatTheKeyMayDo_FromItsScopes(string scope, bool canTrade, bool canWithdraw)
    {
        (DeribitAdapter adapter, _) = Adapter(path => path.EndsWith("/public/auth") ? (HttpStatusCode.OK, Auth.Replace("account:read trade:read wallet:none block_trade:none", scope)) : null);

        CredentialCheck check = await adapter.CheckCredentialsAsync(Credentials, CancellationToken.None);

        check.Should().Be(new CredentialCheck(canTrade, canWithdraw));
        check.Verified.Should().BeTrue();
    }

    [Fact]
    public async Task CheckCredentials_WithoutReadScopes_IsRejected()
    {
        (DeribitAdapter adapter, _) = Adapter(path => path.EndsWith("/public/auth") ? (HttpStatusCode.OK, Auth.Replace("account:read trade:read", "account:none trade:none")) : null);

        VenueException e = (await FluentActions.Invoking(() => adapter.CheckCredentialsAsync(Credentials, CancellationToken.None)).Should().ThrowAsync<VenueException>()).Which;

        e.Failure.Should().Be(VenueFailure.Rejected);
        e.Message.Should().Contain("account:read").And.Contain("trade:read");
    }

    [Fact]
    public async Task WrongKey_IsRejected_AndTheSecretIsNotInTheMessage()
    {
        (DeribitAdapter adapter, _) = Adapter(path => path.EndsWith("/public/auth") ? (HttpStatusCode.BadRequest, """{"jsonrpc":"2.0","error":{"message":"invalid_credentials","code":13004}}""") : null);

        VenueException e = (await FluentActions.Invoking(() => adapter.GetSnapshotAsync(Credentials, CancellationToken.None)).Should().ThrowAsync<VenueException>()).Which;

        e.Failure.Should().Be(VenueFailure.Rejected);
        e.Message.Should().Be("invalid_credentials (code 13004) on public/auth.");
        e.Message.Should().NotContain("client-secret");
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, """{"jsonrpc":"2.0","error":{"message":"too_many_requests","code":10028}}""")]
    [InlineData(HttpStatusCode.BadGateway, "<html>bad gateway</html>")]
    [InlineData(HttpStatusCode.OK, """{"jsonrpc":"2.0"}""")]
    public async Task RateLimit_OrAnUnreadableAnswer_OnPositions_FailsTheWholeSnapshot(HttpStatusCode status, string body)
    {
        (DeribitAdapter adapter, _) = Adapter(path => path.EndsWith("/private/get_positions") ? (status, body) : null);

        VenueException e = (await FluentActions.Invoking(() => adapter.GetSnapshotAsync(Credentials, CancellationToken.None)).Should().ThrowAsync<VenueException>()).Which;

        e.Failure.Should().Be(VenueFailure.Unavailable);
    }

    [Fact]
    public async Task VenueThatDoesNotAnswer_IsUnavailable()
    {
        DeribitAdapter adapter = new(new HttpClient(new StubHandler(_ => throw new HttpRequestException("connection refused"))), TimeProvider.System);

        VenueException e = (await FluentActions.Invoking(() => adapter.GetSnapshotAsync(Credentials, CancellationToken.None)).Should().ThrowAsync<VenueException>()).Which;

        e.Failure.Should().Be(VenueFailure.Unavailable);
        e.Message.Should().Be("Deribit did not answer public/auth.");
    }

    [Fact]
    public async Task PositionOfAnUnknownKind_FailsRatherThanBeingLeftOut()
    {
        string odd = """{"jsonrpc":"2.0","result":[{"instrument_name":"BTC-SOMETHING","kind":"warrant","size":1.0,"mark_price":1.0}]}""";
        (DeribitAdapter adapter, _) = Adapter(path => path.EndsWith("/private/get_positions") ? (HttpStatusCode.OK, odd) : null);

        VenueException e = (await FluentActions.Invoking(() => adapter.GetSnapshotAsync(Credentials, CancellationToken.None)).Should().ThrowAsync<VenueException>()).Which;

        e.Message.Should().Contain("unknown kind");
    }
}
