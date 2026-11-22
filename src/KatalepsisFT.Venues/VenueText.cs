using System.Globalization;

namespace KatalepsisFT.Venues;

internal static class VenueText
{
    private static readonly HashSet<string> Stablecoins = new(StringComparer.OrdinalIgnoreCase) { "USD", "USDT", "USDC", "USDE", "FDUSD", "DAI", "TUSD", "USDP", "PYUSD" };

    /// <summary>Balances in these count as cash: they add to NAV and carry no market exposure.</summary>
    public static bool IsCash(string asset) => Stablecoins.Contains(asset);

    /// <summary>"27DEC24" or "5JAN25" as Deribit writes expiries.</summary>
    public static DateOnly? DayMonthYear(string text) =>
        DateOnly.TryParseExact(text, ["dMMMyy", "ddMMMyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly day) ? day : null;

    /// <summary>"241227" as OKX writes expiries.</summary>
    public static DateOnly? YearMonthDay(string text) =>
        DateOnly.TryParseExact(text, "yyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly day) ? day : null;
}
