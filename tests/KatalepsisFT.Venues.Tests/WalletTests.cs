using System.Text.Json;
using FluentAssertions;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Domain.Instruments;
using KatalepsisFT.Venues.Chains;

namespace KatalepsisFT.Venues.Tests;

public class WalletTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly EvmChain Arbitrum = ChainData.EvmChains.Single(c => c.Name == "Arbitrum");

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Theory]
    [InlineData("0x0", 18, "0")]
    [InlineData("0xde0b6b3a7640000", 18, "1")]
    [InlineData("0x16345785d8a0000", 18, "0.1")]
    [InlineData("0x00000000000000000000000000000000000000000000000000000000000f4240", 6, "1")]
    [InlineData("0xf4240", 6, "1")]
    [InlineData("0x8ac7230489e80000", 18, "10")]
    public void Units_TurnsSmallestUnitsIntoCoins(string hex, int decimals, string expected)
    {
        WalletMath.Units(hex, decimals).Should().Be(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void EvmRequests_AskForTheNativeBalance_ThenEachTokensBalanceOfTheAddress()
    {
        string json = JsonSerializer.Serialize(WalletMath.EvmRequests(Arbitrum, "0xd8da6bf26964af9d7eed9e03e53415d37aa96045"));

        json.Should().Contain("\"method\":\"eth_getBalance\"");
        json.Should().Contain("\"data\":\"0x70a08231000000000000000000000000d8da6bf26964af9d7eed9e03e53415d37aa96045\"");
        JsonDocument.Parse(json).RootElement.GetArrayLength().Should().Be(Arbitrum.Tokens.Count + 1);
    }

    [Fact]
    public void EvmBalances_AddWrappedAndBridgedFormsToTheCoinItself()
    {
        // Arbitrum's list: USDC, USDC.e, USDT, WETH, WBTC, ARB. Answers arrive in any order.
        JsonElement answers = Json("""
            [{"jsonrpc":"2.0","id":4,"result":"0x0000000000000000000000000000000000000000000000000de0b6b3a7640000"},
             {"jsonrpc":"2.0","id":0,"result":"0x1bc16d674ec80000"},
             {"jsonrpc":"2.0","id":1,"result":"0x00000000000000000000000000000000000000000000000000000000000f4240"},
             {"jsonrpc":"2.0","id":2,"result":"0x00000000000000000000000000000000000000000000000000000000001e8480"},
             {"jsonrpc":"2.0","id":3,"result":"0x0"},
             {"jsonrpc":"2.0","id":5,"result":"0x0000000000000000000000000000000000000000000000000000000002faf080"},
             {"jsonrpc":"2.0","id":6,"result":"0x0"}]
            """);

        Dictionary<string, decimal> balances = WalletMath.EvmBalances(Arbitrum, answers);

        // 2 ETH native + 1 WETH; 1 USDC + 2 USDC.e; 0.5 WBTC.
        balances.Should().BeEquivalentTo(new Dictionary<string, decimal> { ["ETH"] = 3m, ["USDC"] = 3m, ["BTC"] = 0.5m });
    }

    [Fact]
    public void EvmBalances_WithAnAnswerMissing_OrAnErrorFromTheNode_Fail()
    {
        JsonElement incomplete = Json("""[{"jsonrpc":"2.0","id":0,"result":"0x0"},{"jsonrpc":"2.0","id":1,"error":{"code":-32000,"message":"execution reverted"}}]""");
        JsonElement refused = Json("""{"jsonrpc":"2.0","id":null,"error":{"code":-32005,"message":"rate limit exceeded"}}""");

        FluentActions.Invoking(() => WalletMath.EvmBalances(Arbitrum, incomplete)).Should().Throw<VenueException>().WithMessage("its node gave no balance for USDC.");
        FluentActions.Invoking(() => WalletMath.EvmBalances(Arbitrum, refused)).Should().Throw<VenueException>().WithMessage("Arbitrum: rate limit exceeded.");
    }

    [Fact]
    public void Solana_AddsUpAnAddressesAccountsForEachToken()
    {
        static string Account(string amount) => """{"account":{"data":{"parsed":{"info":{"mint":"x","tokenAmount":{"amount":"1","decimals":6,"uiAmountString":"AMOUNT"}}}}}}""".Replace("AMOUNT", amount);
        JsonElement answers = Json(
            """
            [{"jsonrpc":"2.0","id":0,"result":{"context":{"slot":1},"value":2500000000}},
             {"jsonrpc":"2.0","id":1,"result":{"value":[A,B]}},
             {"jsonrpc":"2.0","id":2,"result":{"value":[]}},
             {"jsonrpc":"2.0","id":3,"result":{"value":[C]}},
             {"jsonrpc":"2.0","id":4,"result":{"value":[]}},
             {"jsonrpc":"2.0","id":5,"result":{"value":[]}}]
            """.Replace("[A,B]", $"[{Account("100.5")},{Account("0.5")}]").Replace("[C]", $"[{Account("42")}]")
        );

        WalletMath.SolanaBalances(answers).Should().BeEquivalentTo(new Dictionary<string, decimal> { ["SOL"] = 2.5m, ["USDC"] = 101m, ["JUP"] = 42m });
    }

    [Fact]
    public void Bitcoin_IsReceivedLessSpent_AndTron_IsTrxAndUsdt()
    {
        JsonElement bitcoin = Json("""{"address":"bc1q","chain_stats":{"funded_txo_count":3,"funded_txo_sum":1682240494,"spent_txo_count":1,"spent_txo_sum":1287005839,"tx_count":4},"mempool_stats":{"funded_txo_sum":5,"spent_txo_sum":0}}""");
        JsonElement tron = Json("""{"data":[{"balance":14527676250,"trc20":[{"TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t":"2500000000"},{"TOtherTokenxxxxxxxxxxxxxxxxxxxxxxxx":"9"}]}],"success":true}""");
        JsonElement unusedTron = Json("""{"data":[],"success":true}""");

        WalletMath.BitcoinBalance(bitcoin).Should().BeEquivalentTo(new Dictionary<string, decimal> { ["BTC"] = 3.95234655m });
        WalletMath.TronBalances(tron).Should().BeEquivalentTo(new Dictionary<string, decimal> { ["TRX"] = 14_527.67625m, ["USDT"] = 2_500m });
        WalletMath.TronBalances(unusedTron).Should().BeEmpty();
    }

    [Fact]
    public void Snapshot_PricesCoins_KeepsStablecoinsAsCash_AndSaysWhatItCouldNotPrice()
    {
        JsonElement tickers = Json("""[{"symbol":"ETHUSDT","price":"1730.48"},{"symbol":"BTCUSDT","price":"80000"},{"symbol":"ETHBTC","price":"0.02"}]""");
        Dictionary<string, decimal> holdings = new() { ["ETH"] = 3m, ["USDC"] = 250m, ["AERO"] = 10m };

        VenueSnapshot snapshot = WalletMath.Snapshot(Now, holdings, WalletMath.Prices(tickers), ["Base was not read: its node did not answer."]);

        snapshot.Positions.Should().Equal(new VenuePosition(InstrumentType.Spot, "ETH", 3m, 1_730.48m), new VenuePosition(InstrumentType.Cash, "USDC", 250m, 1m));
        snapshot.Notes.Should().HaveCount(3);
        snapshot.Notes![0].Should().Be("Base was not read: its node did not answer.");
        snapshot.Notes[1].Should().Be("10 AERO is held but has no price on Binance, so it is not counted.");
    }

    [Theory]
    [InlineData(typeof(EvmWalletAdapter), "0x1234")]
    [InlineData(typeof(SolanaWalletAdapter), "0xd8dA6BF26964aF9D7eEd9e03E53415D37aA96045")]
    [InlineData(typeof(BitcoinWalletAdapter), "0xd8dA6BF26964aF9D7eEd9e03E53415D37aA96045")]
    [InlineData(typeof(TronWalletAdapter), "bc1qxy2kgdygjrsqtzq2n0yrf2493p83kkfjhx0wlh")]
    public async Task AddressOfTheWrongShape_IsRefusedWithoutCallingAnyone(Type adapterType, string address)
    {
        IVenueAdapter adapter = (IVenueAdapter)Activator.CreateInstance(adapterType, new HttpClient(new NoNetwork()), TimeProvider.System)!;

        VenueException e = (await FluentActions.Invoking(() => adapter.CheckCredentialsAsync(new VenueCredentials(address, string.Empty), CancellationToken.None)).Should().ThrowAsync<VenueException>()).Which;

        e.Failure.Should().Be(VenueFailure.Rejected);
        adapter.UsesAddress.Should().BeTrue();
    }

    [Fact]
    public async Task Transfers_AreSaidToBeUnread_NotReportedAsNone()
    {
        IVenueAdapter adapter = new BitcoinWalletAdapter(new HttpClient(new NoNetwork()), TimeProvider.System);

        VenueException e = (await FluentActions.Invoking(() => adapter.GetTransfersAsync(new VenueCredentials("bc1qxy2kgdygjrsqtzq2n0yrf2493p83kkfjhx0wlh", string.Empty), Now, CancellationToken.None)).Should().ThrowAsync<VenueException>()).Which;

        e.Message.Should().Be(WalletAdapter.NoTransferHistory);
    }

    /// <summary>Real, well-known public addresses on each chain. Off unless LIVE_VENUE_CHECKS=1.</summary>
    [Theory]
    [InlineData(typeof(EvmWalletAdapter), "0xd8dA6BF26964aF9D7eEd9e03E53415D37aA96045", "ETH")]
    [InlineData(typeof(BitcoinWalletAdapter), "bc1qxy2kgdygjrsqtzq2n0yrf2493p83kkfjhx0wlh", "BTC")]
    [InlineData(typeof(TronWalletAdapter), "TNaRAoLUyYEV2uF7GUrzSjRQTU8v5ZJ5VR", "TRX")]
    [InlineData(typeof(SolanaWalletAdapter), "5Q544fKrFoe6tsEbD7S8EmxGTJYAKtTVhAW5Q5pge4j1", "SOL")]
    public async Task Live_RealAddress_IsReadAndValued(Type adapterType, string address, string expectedAsset)
    {
        if (Environment.GetEnvironmentVariable("LIVE_VENUE_CHECKS") != "1")
        {
            return;
        }

        IVenueAdapter adapter = (IVenueAdapter)Activator.CreateInstance(adapterType, new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, TimeProvider.System)!;

        VenueSnapshot snapshot = await adapter.GetSnapshotAsync(new VenueCredentials(address, string.Empty), CancellationToken.None);

        Console.WriteLine($"{adapter.DisplayName}: {string.Join(", ", snapshot.Positions.Select(p => $"{p.Quantity:0.####} {p.Asset} @ {p.MarkPrice:0.####}"))} | {string.Join(" / ", snapshot.Notes ?? [])}");
        snapshot.Positions.Should().Contain(p => p.Asset == expectedAsset && p.Quantity > 0m && p.MarkPrice > 0m);
        snapshot.Notes.Should().NotContain(n => n.Contains("was not read"));
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new InvalidOperationException("No request should be made.");
    }
}
