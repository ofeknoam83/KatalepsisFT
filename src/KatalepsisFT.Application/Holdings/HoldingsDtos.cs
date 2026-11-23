using KatalepsisFT.Analytics.Holdings;
using KatalepsisFT.Domain.Funds;
using KatalepsisFT.Domain.Instruments;

namespace KatalepsisFT.Application.Holdings;

/// <summary>Which instruments count towards exposure. NAV is never filtered.</summary>
public enum InstrumentFilter
{
    All,
    Spot,
    Perp,
    Option,
    TradFi,
}

public sealed record TotalsDto(decimal NavUsd, decimal LongUsd, decimal ShortUsd, decimal GrossUsd, decimal NetUsd, decimal CashUsd)
{
    public static TotalsDto From(BookTotals totals) => new(totals.NavUsd, totals.LongUsd, totals.ShortUsd, totals.GrossUsd, totals.NetUsd, totals.CashUsd);
}

public sealed record FundRefDto(int Id, string Name, bool IsDemo, string? Benchmark);

public sealed record SleeveRefDto(int Id, string Name, MandateType MandateType);

/// <param name="RetiredOn">The day the manager was retired; null while it is active.</param>
public sealed record ManagerRefDto(int Id, string Name, int SleeveId, DateOnly? RetiredOn = null);

/// <param name="OwnerName">For a separately managed account: whose money it is.</param>
public sealed record FundListItemDto(int Id, string Name, bool IsDemo, decimal NavUsd, DateOnly? AsOf, int Sleeves, int Managers, KatalepsisFT.Domain.Funds.FundKind Kind = KatalepsisFT.Domain.Funds.FundKind.Fund, string? OwnerName = null);

public sealed record TypeExposureDto(InstrumentType Type, decimal LongUsd, decimal ShortUsd, decimal GrossUsd, decimal NetUsd);

public sealed record SleeveRowDto(SleeveRefDto Sleeve, int Managers, TotalsDto Totals);

/// <param name="Change1D">Fractions: 0.05 is +5%. Null when the history does not reach back that far.</param>
public sealed record AssetPriceDto(string Symbol, string Name, decimal? Price, decimal? Change1D, decimal? Change30D, decimal? ChangeYtd, decimal? AverageDailyVolumeUsd);

public sealed record HolderDto(int ManagerId, string Manager, decimal NetUsd);

public sealed record AssetRowDto(
    AssetPriceDto Asset,
    decimal LongUsd,
    decimal ShortUsd,
    decimal GrossUsd,
    decimal NetUsd,
    IReadOnlyDictionary<InstrumentType, decimal> NetByType,
    IReadOnlyList<HolderDto> Holders,
    IReadOnlyList<int> SleeveIds,
    bool HasOffsettingManagers
);

public sealed record FundOverviewDto(
    FundRefDto Fund,
    DateOnly AsOf,
    TotalsDto Totals,
    IReadOnlyList<SleeveRowDto> Sleeves,
    IReadOnlyList<AssetRowDto> TopAssets,
    IReadOnlyList<TypeExposureDto> ByType
);

public sealed record FundHoldingsDto(FundRefDto Fund, DateOnly AsOf, TotalsDto Totals, IReadOnlyList<SleeveRefDto> Sleeves, IReadOnlyList<AssetRowDto> Assets);

public sealed record MandateLimitDto(MandateLimitKind Kind, decimal Value);

public sealed record ManagerRowDto(ManagerRefDto Manager, ManagerDataSource DataSource, TotalsDto Totals);

public sealed record OverlapPairDto(int ManagerA, int ManagerB, decimal Share, int OppositeSides);

public sealed record SleeveDetailDto(
    FundRefDto Fund,
    SleeveRefDto Sleeve,
    DateOnly AsOf,
    decimal FundNavUsd,
    TotalsDto Totals,
    IReadOnlyList<MandateLimitDto> Limits,
    IReadOnlyList<ManagerRowDto> Managers,
    IReadOnlyList<AssetRowDto> Assets,
    IReadOnlyList<TypeExposureDto> ByType,
    IReadOnlyList<OverlapPairDto> Overlaps
);

public sealed record PositionDto(
    string Asset,
    string Instrument,
    InstrumentType Type,
    decimal Quantity,
    decimal? EntryPrice,
    decimal MarkPrice,
    decimal MarketValueUsd,
    decimal ExposureUsd,
    decimal UnrealizedPnlUsd
);

public sealed record ManagerDetailDto(
    FundRefDto Fund,
    SleeveRefDto Sleeve,
    ManagerRefDto Manager,
    ManagerDataSource DataSource,
    DateOnly InceptionDate,
    DateOnly AsOf,
    decimal SleeveNavUsd,
    decimal FundNavUsd,
    TotalsDto Totals,
    IReadOnlyList<PositionDto> Positions,
    IReadOnlyList<AssetRowDto> Assets
);

public sealed record AssetPositionDto(ManagerRefDto Manager, decimal ManagerNavUsd, string Instrument, InstrumentType Type, decimal Quantity, decimal ExposureUsd);

public sealed record AssetSleeveDto(SleeveRefDto Sleeve, decimal SleeveNavUsd, decimal LongUsd, decimal ShortUsd, IReadOnlyList<AssetPositionDto> Positions);

public sealed record AssetDetailDto(
    FundRefDto Fund,
    AssetPriceDto Asset,
    DateOnly AsOf,
    decimal FundNavUsd,
    decimal LongUsd,
    decimal ShortUsd,
    decimal GrossUsd,
    decimal NetUsd,
    int Managers,
    IReadOnlyList<AssetSleeveDto> Sleeves,
    IReadOnlyList<TypeExposureDto> ByType
);
