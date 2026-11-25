using System.Text.Json;
using KatalepsisFT.Analytics.Valuation;
using KatalepsisFT.Application.Execution;
using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Domain.Execution;
using KatalepsisFT.Domain.Instruments;
using KatalepsisFT.Domain.Positions;
using Microsoft.EntityFrameworkCore;

namespace KatalepsisFT.Application.Connections;

/// <summary>
/// Turns the latest snapshots of a manager's connections, its hand-entered positions and what its trading accounts
/// hold into today's valued positions and NAV. Today's rows are replaced on every write; when the UTC day changes,
/// yesterday's last write stays as its end-of-day record.
/// </summary>
/// <param name="books">Live prices for what trading accounts hold; without them the latest daily close is used.</param>
public sealed class ManagerBookWriter(IFtDbContext database, IBookFeed? books = null)
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
            // Its leaving was recorded when it was retired, for the whole book at once. Its trading accounts are no
            // longer counted, so that they come back in as money arriving if the manager is reinstated.
            foreach (TradingAccount account in await database.TradingAccounts.Where(a => a.ManagerAccountId == managerAccountId && a.InBookEquityUsd != null).ToListAsync(cancellationToken))
            {
                account.InBookEquityUsd = null;
                account.InBookMode = null;
            }

            await database.SaveChangesAsync(cancellationToken);
            return (0m, 0m, 0m);
        }

        List<string> snapshots = await database
            .VenueConnections.Where(c => c.ManagerAccountId == managerAccountId && c.State != ConnectionState.Disabled && c.LastSnapshotJson != null)
            .Select(c => c.LastSnapshotJson!)
            .ToListAsync(cancellationToken);
        List<VenuePosition> positions = snapshots.SelectMany(json => JsonSerializer.Deserialize<VenueSnapshot>(json, Json)!.Positions).ToList();
        positions.AddRange(await ManualAsync(managerAccountId, cancellationToken));
        positions.AddRange(await TradingAsync(managerAccountId, day, cancellationToken));

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

    /// <summary>
    /// What the manager's trading accounts hold, in the shape connections report. An account is counted when nothing
    /// else already counts it:
    /// a real-money account, unless the manager also has a read-only key on that venue, which then reads the same
    /// account and everything traded in it; a paper account, only when its fund has chosen to count paper trading.
    /// A counted account is counted whole: its positions and its cash, so that its value in the book is what the
    /// account is worth.
    /// </summary>
    private async Task<List<VenuePosition>> TradingAsync(int managerAccountId, DateOnly day, CancellationToken cancellationToken)
    {
        List<TradingAccount> accounts = await database.TradingAccounts.Where(a => a.ManagerAccountId == managerAccountId).ToListAsync(cancellationToken);
        if (accounts.Count == 0)
        {
            return [];
        }

        bool countPaper = await database.ManagerAccounts.Where(m => m.Id == managerAccountId).Select(m => m.Sleeve.Fund.IncludePaperTrading).FirstAsync(cancellationToken);
        // Only a connection that has actually been read counts the account. One that was added and has not
        // succeeded yet reads nothing, and must not push the trading account out of the book meanwhile.
        List<Venue> read = await database.VenueConnections.Where(c => c.ManagerAccountId == managerAccountId && c.State != ConnectionState.Disabled && c.LastSnapshotJson != null).Select(c => c.Venue).Distinct().ToListAsync(cancellationToken);
        int[] ids = accounts.Select(a => a.Id).ToArray();
        List<TradingPosition> held = await database.TradingPositions.AsNoTracking().Where(p => ids.Contains(p.TradingAccountId)).ToListAsync(cancellationToken);
        Dictionary<string, decimal> closes = await LatestClosesAsync(database, held.Select(p => p.Asset).Distinct().ToArray(), cancellationToken);
        List<VenuePosition> result = [];
        foreach (TradingAccount account in accounts)
        {
            // A real-money account that has not been read yet has no value to count.
            bool counted = account.Mode == TradingMode.Live ? !read.Contains(account.Venue) && account.LiveEquityUsd is not null : countPaper;
            List<VenuePosition> lines = [];
            decimal inPositions = 0m;
            foreach (TradingPosition position in held.Where(p => p.TradingAccountId == account.Id && p.Quantity != 0m))
            {
                decimal mark = await MarkAsync(account, position, closes, cancellationToken);
                if (position.InstrumentType == InstrumentType.Perp)
                {
                    decimal open = position.Quantity * (mark - position.AveragePrice);
                    lines.Add(new VenuePosition(InstrumentType.Perp, position.Asset, position.Quantity, mark, position.AveragePrice, open));
                    inPositions += open;
                }
                else
                {
                    lines.Add(new VenuePosition(InstrumentType.Spot, position.Asset, position.Quantity, mark, position.AveragePrice));
                    inPositions += position.Quantity * mark;
                }
            }

            // On paper: what was put in, what has been made and paid in fees, less what the coins held cost. For real
            // money: what the venue says the account is worth, less what is in positions.
            decimal equity = account.Mode == TradingMode.Live
                ? account.LiveEquityUsd ?? 0m
                : account.PaperBalanceUsd + held.Where(p => p.TradingAccountId == account.Id).Sum(p => p.RealisedPnlUsd - p.FeesUsd) + lines.Sum(l => l.Type == InstrumentType.Perp ? l.UnsettledPnlUsd!.Value : l.Quantity * (l.MarkPrice - l.EntryPrice!.Value));
            decimal cash = equity - inPositions;
            if (cash != 0m)
            {
                lines.Add(new VenuePosition(InstrumentType.Cash, account.PerpQuote == "USDC" ? "USDC" : "USDT", Math.Round(cash, 2), 1m));
            }

            string name = $"{account.Venue} trading account \"{account.Label}\"";
            if (counted)
            {
                result.AddRange(lines);
                // Counted before as paper and now as real money, or the other way round: what it was worth then
                // leaves, and what it is worth now arrives. Neither is a gain or a loss.
                if (account.InBookEquityUsd is decimal before && account.InBookMode is { } mode && mode != account.Mode)
                {
                    database.CashFlows.Add(ConnectionSyncService.BookChange(managerAccountId, -before, new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), $"{name} left the book as a {(mode == TradingMode.Live ? "real-money" : "paper")} account"));
                    account.InBookEquityUsd = null;
                }

                if (account.InBookEquityUsd is null)
                {
                    database.CashFlows.Add(ConnectionSyncService.BookChange(managerAccountId, equity, new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), $"{name} added to the book"));
                }

                account.InBookEquityUsd = Math.Round(equity, 2);
                account.InBookMode = account.Mode;
            }
            else if (account.InBookEquityUsd is decimal last)
            {
                database.CashFlows.Add(ConnectionSyncService.BookChange(managerAccountId, -last, new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), $"{name} taken out of the book"));
                account.InBookEquityUsd = null;
                account.InBookMode = null;
            }
        }

        return result;
    }

    /// <summary>The price of a held position: the account's own venue first, then any venue, then the last daily close, then what it cost.</summary>
    private async Task<decimal> MarkAsync(TradingAccount account, TradingPosition position, Dictionary<string, decimal> closes, CancellationToken cancellationToken)
    {
        if (books is not null)
        {
            IReadOnlyList<VenueBook> live = await books.ReadAsync(position.Asset, position.InstrumentType, cancellationToken);
            if ((ConsolidatedBook.Pick(live, account.Venue, account.QuoteFor(position.InstrumentType))?.Mid ?? ConsolidatedBook.Mid(live.ToList())) is decimal mid)
            {
                return mid;
            }
        }

        return closes.TryGetValue(position.Asset, out decimal close) ? close : position.AveragePrice;
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
