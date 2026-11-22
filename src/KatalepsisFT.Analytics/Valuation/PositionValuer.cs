using KatalepsisFT.Domain.Instruments;

namespace KatalepsisFT.Analytics.Valuation;

/// <summary>What a position needs to be valued. Quantity is signed: negative is short.</summary>
public sealed record PositionInput(
    InstrumentType Type,
    decimal Quantity,
    decimal MarkPrice,
    decimal Multiplier = 1m,
    decimal? EntryPrice = null,
    decimal? UnderlyingPrice = null,
    decimal? Delta = null
);

/// <param name="MarketValueUsd">What the position adds to NAV.</param>
/// <param name="ExposureUsd">Signed, delta-adjusted exposure to the underlying asset.</param>
public sealed record PositionValue(decimal MarketValueUsd, decimal ExposureUsd, decimal UnrealizedPnlUsd);

/// <summary>
/// Values one position. NAV and exposure are two different numbers and are kept apart:
/// <list type="bullet">
/// <item>spot, equity: both are quantity × price;</item>
/// <item>cash: counts in NAV, has no exposure;</item>
/// <item>perp, future: NAV gets only the unrealised PnL, quantity × (mark − entry); exposure is the full notional;</item>
/// <item>option: NAV gets the premium, quantity × mark; exposure is delta × quantity × underlying price.</item>
/// </list>
/// The NAV rules are those of Katalepsis' AccountValuator (Horizen.Ria/Execution/AccountValuation.cs:66-139), extended
/// with contract multipliers, dated futures, equities and delta.
/// </summary>
public static class PositionValuer
{
    public static PositionValue Value(PositionInput position)
    {
        decimal units = position.Quantity * position.Multiplier;
        switch (position.Type)
        {
            case InstrumentType.Spot:
            case InstrumentType.Equity:
            {
                decimal value = units * position.MarkPrice;
                decimal pnl = position.EntryPrice is decimal entry ? units * (position.MarkPrice - entry) : 0m;
                return Round(value, value, pnl);
            }

            case InstrumentType.Cash:
                return Round(units * position.MarkPrice, 0m, 0m);

            case InstrumentType.Perp:
            case InstrumentType.Future:
            {
                // Without an entry price the unrealised PnL is unknown and taken as zero, as Katalepsis does.
                decimal pnl = position.EntryPrice is decimal entry && entry > 0m ? units * (position.MarkPrice - entry) : 0m;
                return Round(pnl, units * position.MarkPrice, pnl);
            }

            case InstrumentType.Option:
            {
                if (position.Delta is not decimal delta || position.UnderlyingPrice is not decimal underlying)
                {
                    throw new ArgumentException("An option needs its delta and the price of its underlying to be valued.", nameof(position));
                }

                decimal value = units * position.MarkPrice;
                decimal pnl = position.EntryPrice is decimal entry ? units * (position.MarkPrice - entry) : 0m;
                return Round(value, delta * units * underlying, pnl);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(position), position.Type, "Unknown instrument type.");
        }
    }

    private static PositionValue Round(decimal value, decimal exposure, decimal pnl) =>
        new(Math.Round(value, 2), Math.Round(exposure, 2), Math.Round(pnl, 2));
}
