using System.Text.Json;
using KatalepsisFT.Analytics.Valuation;
using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Domain.Instruments;
using KatalepsisFT.Domain.Positions;
using Microsoft.EntityFrameworkCore;

namespace KatalepsisFT.Application.Connections;

/// <summary>
/// Turns the latest snapshots of a manager's connections into today's valued positions and NAV. Today's rows are
/// replaced on every write; when the UTC day changes, yesterday's last write stays as its end-of-day record.
/// </summary>
public sealed class ManagerBookWriter(IFtDbContext database)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <returns>The book's NAV and its gross and net exposure.</returns>
    public async Task<(decimal NavUsd, decimal GrossUsd, decimal NetUsd)> WriteAsync(int managerAccountId, DateOnly day, CancellationToken cancellationToken)
    {
        // Two services can write one manager's book in the same instant: an exchange read and a wallet read, a read
        // and an edit in the browser, two instances during a release. Each write holds the manager's lock from before
        // it looks at what is stored until it has saved, so a second writer waits and then starts from the first
        // one's result. The retry below is for whatever the lock does not cover, such as a connection that dropped.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await database.WithManagerLockAsync(managerAccountId, () => WriteOnceAsync(managerAccountId, day, cancellationToken), cancellationToken);
            }
            catch (DbUpdateException) when (attempt < 3)
            {
                database.DiscardChanges();
                await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1) + Random.Shared.Next(100)), cancellationToken);
            }
        }
    }

    private async Task<(decimal NavUsd, decimal GrossUsd, decimal NetUsd)> WriteOnceAsync(int managerAccountId, DateOnly day, CancellationToken cancellationToken)
    {
        // A retired manager keeps its history and holds nothing: whatever is still stored for it stays out of the book.
        if (!await database.ManagerAccounts.Where(m => m.Id == managerAccountId).Select(m => m.IsActive).FirstOrDefaultAsync(cancellationToken))
        {
            return (0m, 0m, 0m);
        }

        List<string> snapshots = await database
            .VenueConnections.Where(c => c.ManagerAccountId == managerAccountId && c.State != ConnectionState.Disabled && c.LastSnapshotJson != null)
            .Select(c => c.LastSnapshotJson!)
            .ToListAsync(cancellationToken);
        List<VenuePosition> positions = snapshots.SelectMany(json => JsonSerializer.Deserialize<VenueSnapshot>(json, Json)!.Positions).ToList();
        positions.AddRange(await ManualAsync(managerAccountId, cancellationToken));

        // The same instrument on two venues is one line in the manager's book. Lines are told apart by the
        // instrument's name, so a balance one source calls cash and another calls spot is still one line: two rows
        // for one instrument on one day cannot be stored, and the book would never be written again.
        var lines = positions
            .Where(p => p.Quantity != 0m)
            .GroupBy(p => SymbolOf(p.Type, p.Asset.ToUpperInvariant(), p.Expiry, p.Strike, p.OptionRight))
            .Select(g =>
            {
                VenuePosition first = g.First();
                decimal quantity = g.Sum(p => p.Quantity);
                decimal absolute = g.Sum(p => Math.Abs(p.Quantity));
                decimal Weighted(Func<VenuePosition, decimal?> pick) => g.Sum(p => Math.Abs(p.Quantity) * (pick(p) ?? 0m)) / absolute;
                return new
                {
                    Key = new { first.Type, Asset = first.Asset.ToUpperInvariant(), first.Expiry, first.Strike, first.OptionRight },
                    Quantity = quantity,
                    Mark = Weighted(p => p.MarkPrice),
                    Entry = g.All(p => p.EntryPrice is not null) ? Weighted(p => p.EntryPrice) : (decimal?)null,
                    // Each source's own profit or loss, added up: the venue's figure where it gives one, otherwise worked
                    // out from that source's own entry price. A blended entry price over a long here and a short there
                    // would give the wrong answer.
                    Unsettled = g.All(p => p.UnsettledPnlUsd is not null || p.EntryPrice is not null) ? g.Sum(p => p.UnsettledPnlUsd ?? p.Quantity * (p.MarkPrice - p.EntryPrice!.Value)) : (decimal?)null,
                    Underlying = g.All(p => p.UnderlyingPrice is not null) ? Weighted(p => p.UnderlyingPrice) : (decimal?)null,
                    Delta = g.All(p => p.Delta is not null) ? Weighted(p => p.Delta) : (decimal?)null,
                };
            })
            // A long on one venue and an equal short on another net to nothing held, but what they have made or lost
            // is still part of the manager's value, so such a line stays.
            .Where(l => l.Quantity != 0m || (l.Key.Type is InstrumentType.Perp or InstrumentType.Future && l.Unsettled is decimal open && open != 0m))
            .ToList();

        database.PositionSnapshots.RemoveRange(await database.PositionSnapshots.Where(p => p.ManagerAccountId == managerAccountId && p.Date == day).ToListAsync(cancellationToken));
        decimal nav = 0m,
            gross = 0m,
            net = 0m;
        foreach (var line in lines)
        {
            Instrument instrument = await EnsureInstrumentAsync(line.Key.Type, line.Key.Asset, line.Key.Expiry, line.Key.Strike, line.Key.OptionRight, cancellationToken);
            PositionValue value = PositionValuer.Value(new PositionInput(instrument.Type, line.Quantity, line.Mark, 1m, line.Entry, line.Underlying ?? line.Mark, line.Delta ?? (instrument.Type == InstrumentType.Option ? 0m : null)));
            // What a perp or future adds to NAV is whatever the venue has not yet paid into the cash balance.
            decimal marketValue = instrument.Type is InstrumentType.Perp or InstrumentType.Future && line.Unsettled is decimal unsettled ? Math.Round(unsettled, 2) : value.MarketValueUsd;
            nav += marketValue;
            gross += Math.Abs(value.ExposureUsd);
            net += value.ExposureUsd;
            database.PositionSnapshots.Add(
                new PositionSnapshot
                {
                    ManagerAccountId = managerAccountId,
                    Instrument = instrument,
                    Date = day,
                    Quantity = Math.Round(line.Quantity, 8),
                    EntryPrice = line.Entry is decimal entry ? Math.Round(entry, 8) : null,
                    MarkPrice = Math.Round(line.Mark, 8),
                    UnderlyingPrice = Math.Round(line.Underlying ?? line.Mark, 8),
                    Delta = line.Delta is decimal delta ? Math.Round(delta, 8) : null,
                    MarketValueUsd = marketValue,
                    ExposureUsd = value.ExposureUsd,
                    UnrealizedPnlUsd = value.UnrealizedPnlUsd,
                }
            );
        }

        NavSnapshot? existing = await database.NavSnapshots.FirstOrDefaultAsync(n => n.ManagerAccountId == managerAccountId && n.Date == day, cancellationToken);
        if (existing is null)
        {
            database.NavSnapshots.Add(new NavSnapshot { ManagerAccountId = managerAccountId, Date = day, NavUsd = nav });
        }
        else
        {
            existing.NavUsd = nav;
        }

        await database.SaveChangesAsync(cancellationToken);
        return (nav, gross, net);
    }

    /// <summary>
    /// The manager's hand-entered positions in the shape connections report. A position without a price of its own
    /// takes the asset's latest daily close; one that has neither was refused when it was entered.
    /// </summary>
    private async Task<List<VenuePosition>> ManualAsync(int managerAccountId, CancellationToken cancellationToken)
    {
        List<ManualPosition> manual = await database.ManualPositions.AsNoTracking().Where(p => p.ManagerAccountId == managerAccountId).ToListAsync(cancellationToken);
        if (manual.Count == 0)
        {
            return [];
        }

        string[] symbols = manual.Select(p => p.Asset).Distinct().ToArray();
        Dictionary<string, decimal> closes = await LatestClosesAsync(database, symbols, cancellationToken);
        List<VenuePosition> result = [];
        foreach (ManualPosition p in manual)
        {
            decimal? feed = closes.TryGetValue(p.Asset, out decimal close) ? close : null;
            decimal? mark = p.Price ?? (p.Type == InstrumentType.Cash ? 1m : p.Type == InstrumentType.Option ? null : feed);
            if (mark is null)
            {
                // Its price feed has no value at the moment. Dropping the line would shrink NAV without a word, so it is carried at its entry price.
                mark = p.EntryPrice ?? throw new InvalidOperationException($"Manual position {p.Id} ({p.Asset}) has no price.");
            }

            result.Add(new VenuePosition(p.Type, p.Asset, p.Quantity, mark.Value, p.EntryPrice, null, p.Expiry, p.Strike, p.OptionRight, p.Type == InstrumentType.Option ? p.UnderlyingPrice ?? feed : null, p.Delta));
        }

        return result;
    }

    /// <summary>The most recent daily close of each asset that has any.</summary>
    public static async Task<Dictionary<string, decimal>> LatestClosesAsync(IFtDbContext database, IReadOnlyCollection<string> symbols, CancellationToken cancellationToken)
    {
        var latest = await database
            .PriceBars.Where(b => symbols.Contains(b.Asset.Symbol))
            .GroupBy(b => b.Asset.Symbol)
            .Select(g => new { Symbol = g.Key, Date = g.Max(b => b.Date) })
            .ToListAsync(cancellationToken);
        Dictionary<string, decimal> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in latest)
        {
            result[entry.Symbol] = await database.PriceBars.Where(b => b.Asset.Symbol == entry.Symbol && b.Date == entry.Date).Select(b => b.Close).FirstAsync(cancellationToken);
        }

        return result;
    }

    /// <summary>What one snapshot adds to NAV, by the same rules as the book.</summary>
    public static decimal NavOf(VenueSnapshot snapshot) =>
        snapshot.Positions.Sum(p =>
            p.Type is InstrumentType.Perp or InstrumentType.Future && p.UnsettledPnlUsd is decimal unsettled
                ? Math.Round(unsettled, 2)
                : PositionValuer.Value(new PositionInput(p.Type, p.Quantity, p.MarkPrice, 1m, p.EntryPrice, p.UnderlyingPrice ?? p.MarkPrice, p.Delta ?? (p.Type == InstrumentType.Option ? 0m : null))).MarketValueUsd
        );

    private static string SymbolOf(InstrumentType type, string assetSymbol, DateOnly? expiry, decimal? strike, OptionRight? right) =>
        type switch
        {
            InstrumentType.Perp => $"{assetSymbol}-PERP",
            InstrumentType.Future => $"{assetSymbol} {expiry:d-MMM-yy} future",
            InstrumentType.Option => $"{assetSymbol} {expiry:d-MMM-yy} {strike:0.########} {(right == OptionRight.Put ? "put" : "call")}",
            _ => assetSymbol,
        };

    private async Task<Instrument> EnsureInstrumentAsync(InstrumentType type, string assetSymbol, DateOnly? expiry, decimal? strike, OptionRight? right, CancellationToken cancellationToken)
    {
        string symbol = SymbolOf(type, assetSymbol, expiry, strike, right);
        Instrument? instrument = database.Instruments.Local.FirstOrDefault(i => i.Symbol == symbol) ?? await database.Instruments.FirstOrDefaultAsync(i => i.Symbol == symbol, cancellationToken);
        if (instrument is not null)
        {
            return instrument;
        }

        Asset asset =
            database.Assets.Local.FirstOrDefault(a => a.Symbol == assetSymbol)
            ?? await database.Assets.FirstOrDefaultAsync(a => a.Symbol == assetSymbol, cancellationToken)
            ?? database.Assets.Add(new Asset { Symbol = assetSymbol, Name = assetSymbol, AssetClass = type == InstrumentType.Cash ? AssetClass.Cash : type == InstrumentType.Equity ? AssetClass.Equity : AssetClass.Crypto }).Entity;
        instrument = new Instrument { Asset = asset, Type = type, Symbol = symbol, Expiry = expiry, Strike = strike, OptionRight = right };
        database.Instruments.Add(instrument);
        return instrument;
    }
}
