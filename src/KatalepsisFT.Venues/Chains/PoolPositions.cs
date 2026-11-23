using System.Globalization;
using System.Numerics;
using System.Text.Json;
using KatalepsisFT.Application.Connections;

namespace KatalepsisFT.Venues.Chains;

/// <summary>One concentrated-liquidity position as its manager contract describes it.</summary>
public sealed record PoolPosition(string Token0, string Token1, int FeeOrTickSpacing, int TickLower, int TickUpper, BigInteger Liquidity, BigInteger Owed0, BigInteger Owed1);

/// <summary>
/// Liquidity-pool positions of Uniswap v3 and Aerodrome's concentrated pools: the calls that read them and the
/// arithmetic that turns a position into the two token amounts it holds right now. Pure, so it is tested on its own.
/// A position is counted as those tokens; fees earned but not yet collected are not included.
/// </summary>
public static class PoolPositions
{
    /// <summary>More positions than this on one contract are not read, and the wallet says so.</summary>
    public const int MaxPerManager = 30;

    private const string BalanceOf = "0x70a08231";
    private const string TokenOfOwnerByIndex = "0x2f745c59";
    private const string Positions = "0x99fbab88";
    private const string Factory = "0xc45a0155";
    private const string GetPoolByFee = "0x1698ee82";
    private const string GetPoolByTickSpacing = "0x28af8d0b";
    private const string Slot0 = "0x3850c7bd";

    public static string CountCall(string owner) => BalanceOf + Pad(owner);

    public static string TokenIdCall(string owner, int index) => TokenOfOwnerByIndex + Pad(owner) + Pad(index);

    public static string PositionCall(BigInteger tokenId) => Positions + tokenId.ToString("x64", CultureInfo.InvariantCulture)[^64..];

    public static string FactoryCall => Factory;

    public static string PoolCall(PositionManager manager, PoolPosition position) =>
        (manager.ByTickSpacing ? GetPoolByTickSpacing : GetPoolByFee) + Pad(position.Token0) + Pad(position.Token1) + Pad(position.FeeOrTickSpacing);

    public static string PriceCall => Slot0;

    public static object EthCall(int id, string to, string data) => new { jsonrpc = "2.0", id, method = "eth_call", @params = new object[] { new { to, data }, "latest" } };

    /// <summary>The answers of a batch by request number; a missing or failed answer is an error, never a zero.</summary>
    public static string[] Results(JsonElement answers, int count, string what)
    {
        if (answers.ValueKind != JsonValueKind.Array)
        {
            throw new VenueException(VenueFailure.Unavailable, $"the node did not answer the {what}.");
        }

        Dictionary<int, JsonElement> byId = answers.EnumerateArray().Where(a => a.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.Number).ToDictionary(a => a.GetProperty("id").GetInt32());
        string[] results = new string[count];
        for (int i = 0; i < count; i++)
        {
            results[i] = byId.TryGetValue(i, out JsonElement answer) && answer.TryGetProperty("result", out JsonElement result) && result.ValueKind == JsonValueKind.String && result.GetString()!.Length > 2
                ? result.GetString()!
                : throw new VenueException(VenueFailure.Unavailable, $"the node gave no answer for one of the {what}.");
        }

        return results;
    }

    public static BigInteger Number(string hex) => BigInteger.Parse("0" + hex[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

    public static string AddressOf(string word) => "0x" + word[^40..];

    /// <summary>The twelve fields of a position; only those that say what it holds are kept.</summary>
    public static PoolPosition ParsePosition(string hex)
    {
        if (hex.Length < 2 + 64 * 12)
        {
            throw new VenueException(VenueFailure.Unavailable, "a pool position came back shorter than expected.");
        }

        string Word(int index) => hex.Substring(2 + 64 * index, 64);
        return new PoolPosition(AddressOf(Word(2)), AddressOf(Word(3)), (int)Signed(Word(4)), (int)Signed(Word(5)), (int)Signed(Word(6)), Number("0x" + Word(7)), Number("0x" + Word(10)), Number("0x" + Word(11)));
    }

    /// <summary>The pool's current price, as the square root it stores, from the first field of its state.</summary>
    public static BigInteger ParseSqrtPrice(string hex) => hex.Length >= 66 ? Number("0x" + hex.Substring(2, 64)) : throw new VenueException(VenueFailure.Unavailable, "a pool's price came back empty.");

    /// <summary>
    /// The smallest units of each token a position holds at the pool's current price. Below its range it is all
    /// token 0, above it all token 1, inside it a mix: the standard concentrated-liquidity formulas.
    /// </summary>
    public static (double Amount0, double Amount1) Amounts(BigInteger liquidity, BigInteger sqrtPriceX96, int tickLower, int tickUpper)
    {
        double l = (double)liquidity;
        double price = (double)sqrtPriceX96 / Math.Pow(2, 96);
        double lower = Math.Pow(1.0001, tickLower / 2.0);
        double upper = Math.Pow(1.0001, tickUpper / 2.0);
        if (price <= lower)
        {
            return (l * (1 / lower - 1 / upper), 0);
        }

        if (price >= upper)
        {
            return (0, l * (upper - lower));
        }

        return (l * (1 / price - 1 / upper), l * (price - lower));
    }

    private static string Pad(string address) => address[2..].ToLowerInvariant().PadLeft(64, '0');

    private static string Pad(int value) => value.ToString("x64", CultureInfo.InvariantCulture);

    /// <summary>A 256-bit two's-complement number: ticks can be negative.</summary>
    private static BigInteger Signed(string word)
    {
        BigInteger value = Number("0x" + word);
        return value >= BigInteger.Pow(2, 255) ? value - BigInteger.Pow(2, 256) : value;
    }
}
