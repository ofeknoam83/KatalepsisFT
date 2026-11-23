using System.Text.Json;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Domain.Funds;
using Microsoft.EntityFrameworkCore;

namespace KatalepsisFT.Application.Risk;

/// <param name="Name">The venue, or "Entered by hand" / "Example data" for value that sits on no connected venue.</param>
/// <param name="IsVenue">False for hand-entered and example value: it is shown, but it is not a counterparty.</param>
/// <param name="Share">Of the NAV of the fund, sleeve or manager asked about.</param>
/// <param name="Accounts">Keys and wallets on this venue.</param>
public sealed record VenueRowDto(string Name, bool IsVenue, decimal NavUsd, decimal Share, int Accounts, IReadOnlyList<string> Managers);

public sealed record VenueExposureDto(decimal NavUsd, IReadOnlyList<VenueRowDto> Venues);

/// <summary>One manager's value on one venue.</summary>
public sealed record VenueHolding(int ManagerId, int SleeveId, string Manager, string Venue, bool IsVenue, decimal NavUsd, int Accounts);

/// <summary>Where a fund's money sits: every manager's value split by the venue that holds it.</summary>
public sealed class VenueBook(IReadOnlyList<VenueHolding> holdings)
{
    public VenueExposureDto For(Func<VenueHolding, bool> scope)
    {
        List<VenueHolding> own = holdings.Where(scope).ToList();
        decimal nav = own.Sum(h => h.NavUsd);
        List<VenueRowDto> rows = own.GroupBy(h => (h.Venue, h.IsVenue))
            .Select(g => new VenueRowDto(
                g.Key.Venue,
                g.Key.IsVenue,
                Math.Round(g.Sum(h => h.NavUsd), 2),
                nav > 0m ? Math.Round(g.Sum(h => h.NavUsd) / nav, 6) : 0m,
                g.Sum(h => h.Accounts),
                g.Select(h => h.Manager).Distinct().Order().ToList()
            ))
            .OrderByDescending(r => r.NavUsd)
            .ToList();
        return new VenueExposureDto(Math.Round(nav, 2), rows);
    }

    /// <summary>The venue holding the largest share of the scope, and that share; null when nothing sits on a venue.</summary>
    public (decimal Share, string Venue)? Largest(Func<VenueHolding, bool> scope)
    {
        VenueRowDto? top = For(scope).Venues.FirstOrDefault(v => v.IsVenue && v.NavUsd > 0m);
        return top is null ? null : (top.Share, top.Name);
    }
}

/// <summary>
/// Splits each manager's value by where it is held. What a manager's keys and wallets report is on their venues; the
/// rest of its NAV is its hand-entered positions (or, in the example fund, invented).
/// </summary>
public sealed class VenueExposureService(IFtDbContext database)
{
    public const string ByHand = "Entered by hand";
    public const string Example = "Example data";

    /// <summary>The name a venue goes by on screen.</summary>
    public static string NameOf(Venue venue) =>
        venue switch
        {
            Venue.Okx => "OKX",
            Venue.CoinbaseExchange => "Coinbase Exchange",
            Venue.CoinbasePrime => "Coinbase Prime",
            Venue.Kucoin => "KuCoin",
            Venue.Mexc => "MEXC",
            Venue.EvmWallet => "Wallets · EVM chains",
            Venue.BitcoinWallet => "Wallets · Bitcoin",
            Venue.SolanaWallet => "Wallets · Solana",
            Venue.TronWallet => "Wallets · Tron",
            _ => venue.ToString(),
        };

    /// <returns>Null when the fund does not exist.</returns>
    public async Task<VenueBook?> LoadAsync(int fundId, CancellationToken cancellationToken)
    {
        if (!await database.Funds.AnyAsync(f => f.Id == fundId, cancellationToken))
        {
            return null;
        }

        List<ManagerAccount> managers = await database.ManagerAccounts.AsNoTracking().Where(m => m.Sleeve.FundId == fundId && m.IsActive).ToListAsync(cancellationToken);
        int[] managerIds = managers.Select(m => m.Id).ToArray();
        // Each manager's NAV on its own latest day, as the fund's figures use it.
        var lastDays = await database.NavSnapshots.Where(n => managerIds.Contains(n.ManagerAccountId)).GroupBy(n => n.ManagerAccountId).Select(g => new { ManagerAccountId = g.Key, Date = g.Max(n => n.Date) }).ToListAsync(cancellationToken);
        List<DateOnly> days = lastDays.Select(d => d.Date).Distinct().ToList();
        Dictionary<int, decimal> navs = (
            await database.NavSnapshots.AsNoTracking().Where(n => managerIds.Contains(n.ManagerAccountId) && days.Contains(n.Date)).Select(n => new { n.ManagerAccountId, n.Date, n.NavUsd }).ToListAsync(cancellationToken)
        )
            .Where(n => lastDays.Any(d => d.ManagerAccountId == n.ManagerAccountId && d.Date == n.Date))
            .ToDictionary(n => n.ManagerAccountId, n => n.NavUsd);
        var connections = await database
            .VenueConnections.AsNoTracking()
            .Where(c => managerIds.Contains(c.ManagerAccountId) && c.State != ConnectionState.Disabled && c.LastSnapshotJson != null)
            .Select(c => new { c.ManagerAccountId, c.Venue, c.LastSnapshotJson })
            .ToListAsync(cancellationToken);

        List<VenueHolding> holdings = [];
        foreach (ManagerAccount manager in managers)
        {
            decimal onVenues = 0m;
            foreach (var venue in connections.Where(c => c.ManagerAccountId == manager.Id).GroupBy(c => c.Venue))
            {
                decimal value = venue.Sum(c => ManagerBookWriter.NavOf(JsonSerializer.Deserialize<VenueSnapshot>(c.LastSnapshotJson!, ManagerBookWriter.Json)!));
                onVenues += value;
                holdings.Add(new VenueHolding(manager.Id, manager.SleeveId, manager.Name, NameOf(venue.Key), true, value, venue.Count()));
            }

            // Whatever the manager is worth beyond its connected accounts was typed in, or belongs to the example fund.
            decimal rest = navs.GetValueOrDefault(manager.Id) - onVenues;
            if (Math.Abs(rest) >= 1m)
            {
                holdings.Add(new VenueHolding(manager.Id, manager.SleeveId, manager.Name, manager.DataSource == ManagerDataSource.Demo ? Example : ByHand, false, rest, 0));
            }
        }

        return new VenueBook(holdings);
    }

    public async Task<VenueExposureDto?> GetAsync(int fundId, CancellationToken cancellationToken) => (await LoadAsync(fundId, cancellationToken))?.For(_ => true);
}
