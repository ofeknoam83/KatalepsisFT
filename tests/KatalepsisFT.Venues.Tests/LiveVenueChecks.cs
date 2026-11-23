using FluentAssertions;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Venues.Coinbase;
using KatalepsisFT.Venues.Deribit;
using KatalepsisFT.Venues.Kucoin;
using KatalepsisFT.Venues.Mexc;
using KatalepsisFT.Venues.Okx;
using KatalepsisFT.Venues.Rest;

namespace KatalepsisFT.Venues.Tests;

/// <summary>
/// Calls the real venues with a key that does not exist. Off unless LIVE_VENUE_CHECKS=1, because it needs the internet
/// and the venues to be reachable from where the tests run.
/// </summary>
public class LiveVenueChecks
{
    private static readonly VenueCredentials Nonsense = new("0000000000000000", "0000000000000000000000000000000000000000000", "Not-a-real-passphrase-1");

    private static bool Enabled => Environment.GetEnvironmentVariable("LIVE_VENUE_CHECKS") == "1";

    [Fact]
    public async Task Deribit_RefusesAKeyThatDoesNotExist()
    {
        if (!Enabled)
        {
            return;
        }

        DeribitAdapter adapter = new(new HttpClient { Timeout = TimeSpan.FromSeconds(20) }, TimeProvider.System);

        VenueException e = (await FluentActions.Invoking(() => adapter.CheckCredentialsAsync(Nonsense, CancellationToken.None)).Should().ThrowAsync<VenueException>()).Which;

        e.Failure.Should().Be(VenueFailure.Rejected, e.Message);
    }

    [Fact]
    public async Task Okx_RefusesAKeyThatDoesNotExist()
    {
        if (!Enabled)
        {
            return;
        }

        VenueException e = (await FluentActions.Invoking(() => new OkxAdapter(TimeProvider.System).CheckCredentialsAsync(Nonsense, CancellationToken.None)).Should().ThrowAsync<VenueException>()).Which;

        e.Failure.Should().Be(VenueFailure.Rejected, e.Message);
    }

    [Fact]
    public async Task Kucoin_RefusesAKeyThatDoesNotExist() => await RefusedAsync(new KucoinAdapter(TimeProvider.System));

    [Fact]
    public async Task Mexc_RefusesAKeyThatDoesNotExist() => await RefusedAsync(new MexcAdapter(TimeProvider.System));

    [Fact]
    public async Task CoinbaseExchange_RefusesAKeyThatDoesNotExist() =>
        await RefusedAsync(new CoinbaseExchangeAdapter(new HttpClient { Timeout = TimeSpan.FromSeconds(20) }, TimeProvider.System), new VenueCredentials("0000000000000000", Convert.ToBase64String(new byte[32]), "passphrase"));

    [Fact]
    public async Task CoinbasePrime_RefusesAKeyThatDoesNotExist() => await RefusedAsync(new CoinbasePrimeAdapter(new HttpClient { Timeout = TimeSpan.FromSeconds(20) }, TimeProvider.System));

    [Fact]
    public async Task Kraken_RefusesAKeyThatDoesNotExist() =>
        await RefusedAsync(new KrakenAdapter(Client(), TimeProvider.System), new VenueCredentials(new string('A', 56), Convert.ToBase64String(new byte[64])));

    [Fact]
    public async Task Gate_RefusesAKeyThatDoesNotExist() => await RefusedAsync(new GateAdapter(Client(), TimeProvider.System), new VenueCredentials(new string('0', 32), new string('0', 64)));

    [Fact]
    public async Task Bitget_RefusesAKeyThatDoesNotExist() => await RefusedAsync(new BitgetAdapter(Client(), TimeProvider.System), new VenueCredentials("bg_" + new string('0', 32), new string('0', 64), "passphrase"));

    private static HttpClient Client() => new() { Timeout = TimeSpan.FromSeconds(20) };

    private static async Task RefusedAsync(IVenueAdapter adapter, VenueCredentials? credentials = null)
    {
        if (!Enabled)
        {
            return;
        }

        VenueException e = (await FluentActions.Invoking(() => adapter.CheckCredentialsAsync(credentials ?? Nonsense, CancellationToken.None)).Should().ThrowAsync<VenueException>()).Which;

        e.Failure.Should().Be(VenueFailure.Rejected, e.Message);
    }

    [Fact]
    public async Task KucoinOrders_ReadItsRulesForSpotAndPerpetuals_AndAreRefusedWithAKeyThatDoesNotExist()
    {
        if (!Enabled)
        {
            return;
        }

        KucoinOrderGateway gateway = new(new HttpClient { Timeout = TimeSpan.FromSeconds(20) }, TimeProvider.System);

        KatalepsisFT.Application.Execution.VenueInstrument spot = (await gateway.InstrumentAsync("BTC", KatalepsisFT.Domain.Instruments.InstrumentType.Spot, CancellationToken.None))!;
        KatalepsisFT.Application.Execution.VenueInstrument perp = (await gateway.InstrumentAsync("BTC", KatalepsisFT.Domain.Instruments.InstrumentType.Perp, CancellationToken.None))!;
        Func<Task> find = () => gateway.FindAsync(Nonsense, spot, KatalepsisFT.Domain.Instruments.InstrumentType.Spot, "kft1-000000000000", CancellationToken.None);
        Func<Task> findPerp = () => gateway.FindAsync(Nonsense, perp, KatalepsisFT.Domain.Instruments.InstrumentType.Perp, "kft1-000000000000", CancellationToken.None);
        Func<Task> account = () => gateway.AccountAsync(Nonsense, CancellationToken.None);

        spot.Symbol.Should().Be("BTC-USDT");
        spot.PriceStep.Should().BeGreaterThan(0m);
        perp.Symbol.Should().Be("XBTUSDTM");
        perp.QuantityStep.Should().Be(0.001m);
        (await gateway.InstrumentAsync("NOSUCHCOIN", KatalepsisFT.Domain.Instruments.InstrumentType.Spot, CancellationToken.None)).Should().BeNull();
        // A key KuCoin does not know is a clear no, on both of its systems; never "not found" and never "unclear".
        (await find.Should().ThrowAsync<KatalepsisFT.Application.Execution.VenueRefusedException>()).Which.Message.Should().Contain("code 4000");
        (await findPerp.Should().ThrowAsync<KatalepsisFT.Application.Execution.VenueRefusedException>()).Which.Message.Should().Contain("code 4000");
        await account.Should().ThrowAsync<KatalepsisFT.Application.Execution.VenueRefusedException>();
    }
}
