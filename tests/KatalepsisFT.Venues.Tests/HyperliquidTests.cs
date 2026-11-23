using System.Text.Json;
using FluentAssertions;
using KatalepsisFT.Analytics.Valuation;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Domain.Instruments;
using KatalepsisFT.Venues.Hyperliquid;

namespace KatalepsisFT.Venues.Tests;

public class HyperliquidTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
    private const string Me = "0x00000000000000000000000000000000000000aa";

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    /// <summary>The first position is copied from a real account's answer; the totals are made to match the two positions.</summary>
    private static readonly JsonElement Perps = Json("""
        {"marginSummary":{"accountValue":"10000.0","totalNtlPos":"13254.96588","totalRawUsd":"11254.96588","totalMarginUsed":"662.7"},
         "withdrawable":"9000.0",
         "assetPositions":[
           {"type":"oneWay","position":{"coin":"BTC","szi":"-0.11772","leverage":{"type":"cross","value":20},"entryPx":"61249.3","positionValue":"7254.96588","unrealizedPnl":"-44.69712","liquidationPx":null,"marginUsed":"362.748294","maxLeverage":50}},
           {"type":"oneWay","position":{"coin":"ETH","szi":"2.0","entryPx":"2900.0","positionValue":"6000.0","unrealizedPnl":"200.0"}},
           {"type":"oneWay","position":{"coin":"SOL","szi":"0.0","entryPx":"0","positionValue":"0.0","unrealizedPnl":"0.0"}}],
         "time":1795413819971}
        """);

    private static readonly JsonElement NoSpot = Json("""{"balances":[]}""");

    [Fact]
    public void PerpAccount_IsCollateralPlusPositions_AndAddsUpToTheAccountValue()
    {
        VenueSnapshot snapshot = HyperliquidMapper.Map(Now, Perps, NoSpot, null, null);

        snapshot.Positions.Should().Equal(
            // 10,000 of account value less 155.30288 of open profit.
            new VenuePosition(InstrumentType.Cash, "USDC", 9_844.69712m, 1m),
            new VenuePosition(InstrumentType.Perp, "BTC", -0.11772m, 7_254.96588m / 0.11772m, 61_249.3m, -44.69712m),
            new VenuePosition(InstrumentType.Perp, "ETH", 2m, 3_000m, 2_900m, 200m)
        );
        snapshot.ReportedEquityUsd.Should().Be(10_000m);
        ManagerBookWriter.NavOf(snapshot).Should().Be(10_000m);
    }

    [Fact]
    public void SpotCoins_ArePricedAtTheirUsdcPair_AndBridgedCoinsTakeTheirOwnName()
    {
        JsonElement spot = Json("""{"balances":[{"coin":"USDC","token":0,"total":"500.0","hold":"0.0"},{"coin":"UBTC","token":197,"total":"0.5","hold":"0.0"},{"coin":"PURR","token":1,"total":"1000","hold":"0"},{"coin":"HYPE","token":150,"total":"0.0","hold":"0"}]}""");
        JsonElement meta = Json("""{"tokens":[{"name":"USDC","index":0},{"name":"PURR","index":1},{"name":"UBTC","index":197}],"universe":[{"tokens":[1,0],"name":"PURR/USDC","index":0},{"tokens":[197,0],"name":"@142","index":142},{"tokens":[197,1],"name":"@999","index":999}]}""");
        JsonElement mids = Json("""{"PURR/USDC":"0.2","@142":"80000.0","@999":"1.0","BTC":"80010.0"}""");

        VenueSnapshot snapshot = HyperliquidMapper.Map(Now, Json("""{"marginSummary":{"accountValue":"0.0"},"assetPositions":[]}"""), spot, meta, mids);

        snapshot.Positions.Should().Equal(new VenuePosition(InstrumentType.Cash, "USDC", 500m, 1m), new VenuePosition(InstrumentType.Spot, "BTC", 0.5m, 80_000m), new VenuePosition(InstrumentType.Spot, "PURR", 1_000m, 0.2m));
        snapshot.ReportedEquityUsd.Should().Be(40_700m);
        HyperliquidMapper.HoldsSpotCoins(spot).Should().BeTrue();
        HyperliquidMapper.HoldsSpotCoins(Json("""{"balances":[{"coin":"USDC","total":"5"}]}""")).Should().BeFalse();
    }

    [Fact]
    public void SpotCoinWithoutAUsdcPrice_FailsTheSnapshot()
    {
        JsonElement spot = Json("""{"balances":[{"coin":"ODD","total":"3"}]}""");

        FluentActions.Invoking(() => HyperliquidMapper.Map(Now, Json("""{"marginSummary":{"accountValue":"0"},"assetPositions":[]}"""), spot, Json("""{"tokens":[],"universe":[]}"""), Json("{}")))
            .Should().Throw<VenueException>().WithMessage("Hyperliquid holds ODD but lists no USDC price for it.");
    }

    [Fact]
    public void Ledger_CountsMoneyCrossingTheAccountsEdge_AndNothingElse()
    {
        JsonElement ledger = Json($$$"""
            [{"time":1795155907274,"hash":"0xa1","delta":{"type":"deposit","usdc":"5000.0"}},
             {"time":1795155907275,"hash":"0xa2","delta":{"type":"withdraw","usdc":"1000.0","nonce":1,"fee":"1.0"}},
             {"time":1795155907276,"hash":"0xa3","delta":{"type":"internalTransfer","usdc":"250.0","user":"0x00000000000000000000000000000000000000bb","destination":"{{{Me}}}","fee":"0.0"}},
             {"time":1795155907277,"hash":"0xa4","delta":{"type":"internalTransfer","usdc":"100.0","user":"{{{Me}}}","destination":"0x00000000000000000000000000000000000000bb","fee":"1.0"}},
             {"time":1795155907278,"hash":"0xa5","delta":{"type":"spotTransfer","token":"UBTC","amount":"0.25","user":"{{{Me}}}","destination":"0x00000000000000000000000000000000000000bb"}},
             {"time":1795155907279,"hash":"0xa6","delta":{"type":"accountClassTransfer","usdc":"777.0","toPerp":true}},
             {"time":1795155907280,"hash":"0xa7","delta":{"type":"vaultDeposit","vault":"0xdfc24b077bc1425ad1dea75bcb6f8158e10df303","usdc":"218.059881"}}]
            """);

        IReadOnlyList<VenueTransfer> transfers = HyperliquidMapper.Transfers(ledger, Me);

        transfers.Select(t => (t.Asset, t.Quantity)).Should().Equal(("USDC", 5_000m), ("USDC", -1_001m), ("USDC", 250m), ("USDC", -101m), ("BTC", -0.25m));
        transfers.Select(t => t.Id).Should().OnlyHaveUniqueItems();
        transfers[0].At.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1795155907274));
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("0x1234")]
    [InlineData("0xZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ")]
    public async Task MalformedAddress_IsRefusedWithoutCallingAnyone(string address)
    {
        HyperliquidAdapter adapter = new(new HttpClient(new NoNetwork()), TimeProvider.System);

        VenueException e = (await FluentActions.Invoking(() => adapter.CheckCredentialsAsync(new VenueCredentials(address, string.Empty), CancellationToken.None)).Should().ThrowAsync<VenueException>()).Which;

        e.Failure.Should().Be(VenueFailure.Rejected);
    }

    /// <summary>
    /// Against the real venue, with public addresses that hold many open positions: the value this system computes
    /// from the positions must equal the value Hyperliquid itself states. Off unless LIVE_VENUE_CHECKS=1.
    /// </summary>
    [Theory]
    [InlineData("0x010461c14e146ac35fe42271bdc1134ee31c703a")]
    [InlineData("0x31ca8395cf837de08b24da3f660e77761dfb974b")]
    public async Task Live_ComputedValue_EqualsTheValueHyperliquidStates(string address)
    {
        if (Environment.GetEnvironmentVariable("LIVE_VENUE_CHECKS") != "1")
        {
            return;
        }

        HyperliquidAdapter adapter = new(new HttpClient { Timeout = TimeSpan.FromSeconds(20) }, TimeProvider.System);
        VenueCredentials wallet = new(address, string.Empty);

        CredentialCheck check = await adapter.CheckCredentialsAsync(wallet, CancellationToken.None);
        VenueSnapshot snapshot = await adapter.GetSnapshotAsync(wallet, CancellationToken.None);
        IReadOnlyList<VenueTransfer> transfers = await adapter.GetTransfersAsync(wallet, DateTimeOffset.UtcNow.AddDays(-30), CancellationToken.None);

        check.Should().Be(new CredentialCheck(false, false));
        snapshot.ReportedEquityUsd.Should().NotBeNull();
        // Each position's contribution is rounded to the cent, so a few hundred positions may differ by a few dollars in total.
        ManagerBookWriter.NavOf(snapshot).Should().BeApproximately(snapshot.ReportedEquityUsd!.Value, 5m);
        snapshot.Positions.Where(p => p.Type == InstrumentType.Perp).Should().OnlyContain(p => p.MarkPrice > 0m && p.Quantity != 0m);
        transfers.All(t => t.Quantity != 0m).Should().BeTrue();
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new InvalidOperationException("No request should be made.");
    }
}
