using System.Numerics;
using System.Text.Json;
using FluentAssertions;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Venues.Chains;

namespace KatalepsisFT.Venues.Tests;

public class PoolPositionTests
{
    /// <summary>
    /// A real USDC/WETH position on Ethereum (Uniswap v3 token 1256191), read on 23 November 2026. The expected
    /// amounts are not from this system's formula: they are what the Uniswap contract itself answered when asked,
    /// in a simulated call, what withdrawing the whole position would return at that same price.
    /// </summary>
    [Fact]
    public void Amounts_MatchWhatTheUniswapContractReturns_ForARealPositionInsideItsRange()
    {
        (double usdc, double weth) = PoolPositions.Amounts(BigInteger.Parse("907323427050498"), BigInteger.Parse("1911987293542693010751289974722182"), 191650, 206190);

        usdc.Should().BeApproximately(7_354_154_713d, 1d);
        (weth / 8_738_410_283_153_027_645d).Should().BeApproximately(1d, 1e-9);
    }

    /// <summary>The same check on a real full-range position (token 1256308) holding almost nothing.</summary>
    [Fact]
    public void Amounts_MatchTheContract_ForARealFullRangePosition()
    {
        (double usdc, double weth) = PoolPositions.Amounts(23, BigInteger.Parse("1911987293542693010751289974722182"), -887220, 887220);

        usdc.Should().BeLessThan(1d);
        weth.Should().BeApproximately(555_051d, 1d);
    }

    [Fact]
    public void Amounts_BelowTheRangeAreAllToken0_AndAboveItAllToken1()
    {
        BigInteger liquidity = BigInteger.Pow(10, 18);
        BigInteger priceOne = BigInteger.Pow(2, 96);

        (double belowA, double belowB) = PoolPositions.Amounts(liquidity, priceOne, 100, 200);
        (double aboveA, double aboveB) = PoolPositions.Amounts(liquidity, priceOne, -200, -100);
        (double insideA, double insideB) = PoolPositions.Amounts(liquidity, priceOne, -10, 10);

        belowA.Should().BePositive();
        belowB.Should().Be(0);
        aboveA.Should().Be(0);
        aboveB.Should().BePositive();
        // Around a price of exactly one, a symmetric range holds the same of both.
        insideA.Should().BeApproximately(insideB, insideA * 1e-9);
        insideA.Should().BeApproximately(1e18 * (1 - Math.Pow(1.0001, -5)), 1e6);
    }

    [Fact]
    public void ParsePosition_ReadsTokensRangeAndLiquidity_WithNegativeTicks()
    {
        string Word(BigInteger value) => (value < 0 ? value + BigInteger.Pow(2, 256) : value).ToString("x64")[^64..];
        string Address(string address) => address[2..].PadLeft(64, '0');
        string hex =
            "0x"
            + Word(0) + Address("0x0000000000000000000000000000000000000000")
            + Address("0xa0b86991c6218b36c1d19d4a2e9eb0ce3606eb48") + Address("0xc02aaa39b223fe8d0a0e5c4f27ead9083c756cc2")
            + Word(500) + Word(-887220) + Word(206190) + Word(BigInteger.Parse("907323427050498"))
            + Word(0) + Word(0) + Word(1500) + Word(2500);

        PoolPosition position = PoolPositions.ParsePosition(hex);

        position.Should().Be(new PoolPosition("0xa0b86991c6218b36c1d19d4a2e9eb0ce3606eb48", "0xc02aaa39b223fe8d0a0e5c4f27ead9083c756cc2", 500, -887220, 206190, BigInteger.Parse("907323427050498"), 1500, 2500));
        FluentActions.Invoking(() => PoolPositions.ParsePosition("0x1234")).Should().Throw<VenueException>();
    }

    [Fact]
    public void Calls_AreBuiltForTheRightKindOfPool()
    {
        PoolPosition position = new("0x4200000000000000000000000000000000000006", "0x833589fCD6eDb6E08f4c7C32D4f71b54bdA02913", 100, 0, 0, 0, 0, 0);
        PositionManager uniswap = ChainData.PositionManagers.Single(m => m.Chain == "Base" && m.Protocol == "Uniswap v3");
        PositionManager aerodrome = ChainData.PositionManagers.Single(m => m.Protocol == "Aerodrome");

        // Uniswap finds a pool by its fee, Aerodrome by its tick spacing: different functions on their factories.
        PoolPositions.PoolCall(uniswap, position).Should().StartWith("0x1698ee82").And.EndWith("64").And.HaveLength(10 + 64 * 3);
        PoolPositions.PoolCall(aerodrome, position).Should().StartWith("0x28af8d0b");
        PoolPositions.TokenIdCall("0xB85cd82c59A9d4fbb3c50DaaA9f6623F6a427b55", 3).Should().Be("0x2f745c59000000000000000000000000b85cd82c59a9d4fbb3c50daaa9f6623f6a427b55" + new string('0', 63) + "3");
        PoolPositions.PositionCall(1256191).Should().Be("0x99fbab88" + 1256191.ToString("x64"));
    }

    [Fact]
    public void Results_WithAnAnswerMissing_Fail()
    {
        JsonElement answers = JsonDocument.Parse("""[{"id":0,"result":"0x01"},{"id":1,"error":{"message":"reverted"}}]""").RootElement;

        PoolPositions.Results(answers, 1, "positions").Should().Equal("0x01");
        FluentActions.Invoking(() => PoolPositions.Results(answers, 2, "positions")).Should().Throw<VenueException>().WithMessage("the node gave no answer for one of the positions.");
    }

    /// <summary>A real address holding an open Uniswap v3 USDC/WETH position. Off unless LIVE_VENUE_CHECKS=1.</summary>
    [Fact]
    public async Task Live_WalletWithAUniswapPosition_CountsTheTokensInsideIt()
    {
        if (Environment.GetEnvironmentVariable("LIVE_VENUE_CHECKS") != "1")
        {
            return;
        }

        EvmWalletAdapter adapter = new(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, TimeProvider.System);

        VenueSnapshot snapshot = await adapter.GetSnapshotAsync(new VenueCredentials("0xb85cd82c59a9d4fbb3c50daaa9f6623f6a427b55", string.Empty), CancellationToken.None);

        Console.WriteLine($"{string.Join(", ", snapshot.Positions.Select(p => $"{p.Quantity:0.####} {p.Asset}"))} | {string.Join(" / ", snapshot.Notes ?? [])}");
        snapshot.Notes.Should().Contain(n => n.Contains("Uniswap v3 position(s) on Ethereum are counted"));
        snapshot.Notes.Should().NotContain(n => n.Contains("were not read") || n.Contains("was not read"));
    }
}
