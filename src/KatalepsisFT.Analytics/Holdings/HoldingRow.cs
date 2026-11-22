using KatalepsisFT.Domain.Instruments;

namespace KatalepsisFT.Analytics.Holdings;

/// <summary>One valued position of one manager on one day, with where it sits in the fund.</summary>
public sealed record HoldingRow(
    int SleeveId,
    string Sleeve,
    int ManagerId,
    string Manager,
    string Asset,
    InstrumentType Type,
    string Instrument,
    decimal Quantity,
    decimal? EntryPrice,
    decimal MarkPrice,
    decimal MarketValueUsd,
    decimal ExposureUsd,
    decimal UnrealizedPnlUsd
);
