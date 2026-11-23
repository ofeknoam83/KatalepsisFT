using System.Globalization;
using System.Net;
using Bybit.Net;
using Bybit.Net.Clients;
using Bybit.Net.Enums;
using Bybit.Net.Objects.Models.V5;
using CryptoExchange.Net.Objects;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Domain.Instruments;
using KatalepsisFT.Infrastructure.Health;

namespace KatalepsisFT.Venues.Bybit;

/// <summary>
/// Bybit through Bybit.Net, the library Katalepsis uses. Only read calls are used, those of Katalepsis'
/// Infrastructure/Services/Bybit/BybitApiClient.cs and Perpetual/BybitPerpetualApiClient.cs (commit 9847e4f): the
/// unified account's balances and positions, plus the key's own description. Reads a unified trading account; the
/// funding account and classic (non-unified) accounts are not read.
/// </summary>
public sealed class BybitAdapter(TimeProvider clock) : IVenueAdapter
{
    /// <summary>Bybit answers an option query for one coin at a time; these are the coins it lists options on.</summary>
    private static readonly string[] OptionCoins = ["BTC", "ETH", "SOL", "XRP", "DOGE", "MNT"];

    private const int PageSize = 200;

    public Venue Venue => Venue.Bybit;

    public string DisplayName => "Bybit";

    public bool NeedsPassphrase => false;

    public string WorkerGroup => WorkerGroups.Cefi;

    public async Task<CredentialCheck> CheckCredentialsAsync(VenueCredentials credentials, CancellationToken cancellationToken)
    {
        using BybitRestClient client = Client(credentials);
        BybitApiKeyInfo info = Data(await client.V5Api.Account.GetApiKeyInfoAsync(cancellationToken), "key description");
        if (info.Readonly)
        {
            return new CredentialCheck(false, false);
        }

        // A key that is not read-only can do whatever its permission lists name; any entry there is a write permission.
        BybitPermissions? p = info.Permissions;
        bool Any(IEnumerable<string>? list) => list is not null && list.Any();
        bool canWithdraw = Any(p?.Wallet);
        bool canTrade = Any(p?.ContractTrade) || Any(p?.Spot) || Any(p?.Options) || Any(p?.Derivatives) || Any(p?.CopyTrading) || Any(p?.BlockTrade) || Any(p?.Exchange);
        return new CredentialCheck(canTrade, canWithdraw);
    }

    public async Task<VenueSnapshot> GetSnapshotAsync(VenueCredentials credentials, CancellationToken cancellationToken)
    {
        using BybitRestClient client = Client(credentials);
        BybitBalance balance =
            Data(await client.V5Api.Account.GetBalancesAsync(AccountType.Unified, ct: cancellationToken), "account balance").List.FirstOrDefault()
            ?? throw new VenueException(VenueFailure.Unavailable, "Bybit returned no unified account for this key.");

        List<(Category Category, BybitPosition Position)> positions = [];
        positions.AddRange(await PositionsAsync(client, Category.Linear, null, "USDT", cancellationToken));
        positions.AddRange(await PositionsAsync(client, Category.Linear, null, "USDC", cancellationToken));
        positions.AddRange(await PositionsAsync(client, Category.Inverse, null, null, cancellationToken));
        foreach (string coin in OptionCoins)
        {
            positions.AddRange(await PositionsAsync(client, Category.Option, coin, null, cancellationToken));
        }

        // An option position carries the option's own price only; the price of what it is an option on is read apart.
        Dictionary<string, decimal> underlying = new(StringComparer.OrdinalIgnoreCase);
        foreach (string coin in positions.Where(p => p.Category == Category.Option && p.Position.Quantity != 0m).Select(p => p.Position.Symbol.Split('-')[0].ToUpperInvariant()).Distinct().ToList())
        {
            underlying[coin] = Data(await client.V5Api.ExchangeData.GetSpotTickersAsync($"{coin}USDT", cancellationToken), $"{coin} price").List.FirstOrDefault()?.LastPrice
                ?? throw new VenueException(VenueFailure.Unavailable, $"Bybit gave no price for {coin}.");
        }

        return BybitMapper.Map(clock.GetUtcNow(), balance, positions, underlying);
    }

    /// <summary>Transfers into and out of the unified account, from its transaction log. Bybit answers for seven days at a time.</summary>
    public async Task<IReadOnlyList<VenueTransfer>> GetTransfersAsync(VenueCredentials credentials, DateTimeOffset since, CancellationToken cancellationToken)
    {
        using BybitRestClient client = Client(credentials);
        DateTime now = clock.GetUtcNow().UtcDateTime;
        DateTime start = since.UtcDateTime < now.AddDays(-7).AddMinutes(5) ? now.AddDays(-7).AddMinutes(5) : since.UtcDateTime;
        List<BybitTransactionLog> entries = [];
        foreach (TransactionLogType type in new[] { TransactionLogType.TransferIn, TransactionLogType.TransferOut })
        {
            string? cursor = null;
            for (int page = 0; page < 20; page++)
            {
                BybitResponse<BybitTransactionLog> response = Data(await client.V5Api.Account.GetTransactionHistoryAsync(AccountType.Unified, type: type, startTime: start, endTime: now, limit: 50, cursor: cursor, ct: cancellationToken), "transfers");
                entries.AddRange(response.List);
                cursor = response.NextPageCursor;
                if (string.IsNullOrEmpty(cursor) || !response.List.Any())
                {
                    break;
                }
            }
        }

        return BybitMapper.Transfers(entries);
    }

    private static async Task<List<(Category Category, BybitPosition Position)>> PositionsAsync(BybitRestClient client, Category category, string? baseAsset, string? settleAsset, CancellationToken cancellationToken)
    {
        List<(Category Category, BybitPosition Position)> all = [];
        string? cursor = null;
        // Far more pages than any account has; the bound only stops a venue that keeps returning a cursor.
        for (int page = 0; page < 50; page++)
        {
            BybitResponse<BybitPosition> response = Data(await client.V5Api.Trading.GetPositionsAsync(category, baseAsset: baseAsset, settleAsset: settleAsset, limit: PageSize, cursor: cursor, ct: cancellationToken), $"{category} positions");
            // The category asked for, not the one on the row: the answer states it once for the whole page.
            all.AddRange(response.List.Select(p => (category, p)));
            cursor = response.NextPageCursor;
            if (string.IsNullOrEmpty(cursor) || !response.List.Any())
            {
                return all;
            }
        }

        throw new VenueException(VenueFailure.Unavailable, $"Bybit kept returning more {category} positions than this system reads.");
    }

    private static BybitRestClient Client(VenueCredentials credentials) =>
        new(options =>
        {
            options.ApiCredentials = new BybitCredentials(credentials.ApiKey, credentials.ApiSecret);
            options.RequestTimeout = TimeSpan.FromSeconds(20);
        });

    private static T Data<T>(WebCallResult<T> result, string what)
    {
        if (result.Success && result.Data is not null)
        {
            return result.Data;
        }

        // 10003 invalid key, 10004 bad signature, 10005 permission denied, 10010 address not allowed, 33004 key expired.
        bool rejected = result.ResponseStatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || result.Error?.Code is 10003 or 10004 or 10005 or 10010 or 33004;
        string reason = result.Error is null ? "no answer" : $"{result.Error.Message} (code {result.Error.Code?.ToString(CultureInfo.InvariantCulture) ?? "none"})";
        throw new VenueException(rejected ? VenueFailure.Rejected : VenueFailure.Unavailable, $"{reason} reading the {what}.");
    }
}

/// <summary>Bybit's answers in the shape every venue reports. Pure, so it is tested without the venue.</summary>
public static class BybitMapper
{
    private static readonly string[] LinearQuotes = ["USDT", "USDC", "PERP"];

    public static VenueSnapshot Map(DateTimeOffset asOf, BybitBalance balance, IEnumerable<(Category Category, BybitPosition Position)> positions, IReadOnlyDictionary<string, decimal>? underlyingPrices = null)
    {
        List<VenuePosition> result = [];
        foreach (BybitAssetBalance asset in balance.Assets ?? [])
        {
            // The wallet balance leaves out open profit, which arrives with the positions below.
            decimal wallet = asset.WalletBalance ?? 0m;
            if (wallet == 0m)
            {
                continue;
            }

            string symbol = asset.Asset.ToUpperInvariant();
            decimal equity = asset.Equity ?? 0m;
            decimal price = equity != 0m && asset.UsdValue is decimal usd ? usd / equity : VenueText.IsCash(symbol) ? 1m : throw new VenueException(VenueFailure.Unavailable, $"Bybit gave no USD value for the {symbol} balance.");
            result.Add(new VenuePosition(VenueText.IsCash(symbol) ? InstrumentType.Cash : InstrumentType.Spot, symbol, wallet, price));
        }

        foreach ((Category category, BybitPosition position) in positions.Where(p => p.Position.Quantity != 0m))
        {
            // Sizes are always positive; the side says which way.
            int sign = position.Side == PositionSide.Sell ? -1 : 1;
            decimal mark = position.MarkPrice ?? throw Missing(position, "a mark price");
            decimal? entry = position.AveragePrice is null or 0m ? null : position.AveragePrice;
            string[] parts = position.Symbol.Split('-');

            switch (category)
            {
                case Category.Linear:
                {
                    // "BTCUSDT", "BTCPERP" (USDC perp) or "BTC-26DEC26" (USDC future). Sizes are in coins.
                    string asset = parts.Length > 1 ? parts[0] : Strip(position.Symbol, LinearQuotes) ?? throw Missing(position, "a readable name");
                    DateOnly? expiry = parts.Length > 1 ? VenueText.DayMonthYear(parts[1]) ?? throw Missing(position, "a readable expiry") : null;
                    result.Add(new VenuePosition(expiry is null ? InstrumentType.Perp : InstrumentType.Future, asset.ToUpperInvariant(), sign * position.Quantity, mark, entry, position.UnrealizedPnl, expiry));
                    break;
                }

                case Category.Inverse:
                {
                    // "BTCUSD" or "BTCUSDZ26": sized in USD, margined and settled in the coin.
                    int usd = position.Symbol.IndexOf("USD", StringComparison.OrdinalIgnoreCase);
                    if (usd <= 0)
                    {
                        throw Missing(position, "a readable name");
                    }

                    bool perpetual = position.Symbol.Length == usd + 3;
                    decimal? unsettled = position.UnrealizedPnl is decimal pnl ? pnl * mark : null;
                    // A dated inverse future's code ("Z26") gives only the month, so it is kept apart from the perp by type alone.
                    result.Add(new VenuePosition(perpetual ? InstrumentType.Perp : InstrumentType.Future, position.Symbol[..usd].ToUpperInvariant(), sign * position.Quantity / mark, mark, entry, unsettled, perpetual ? null : QuarterEnd(position.Symbol[(usd + 3)..]) ?? throw Missing(position, "a readable expiry")));
                    break;
                }

                case Category.Option:
                {
                    // "BTC-26DEC26-90000-C", newer ones with a "-USDT" at the end. Premiums are in USD.
                    if (parts.Length < 4)
                    {
                        throw Missing(position, "a readable name");
                    }

                    decimal delta = position.Delta ?? throw Missing(position, "a delta");
                    decimal quantity = sign * position.Quantity;
                    decimal value = position.PositionValue ?? throw Missing(position, "a position value");
                    result.Add(
                        new VenuePosition(
                            InstrumentType.Option,
                            parts[0].ToUpperInvariant(),
                            quantity,
                            Math.Abs(value / position.Quantity),
                            entry,
                            null,
                            VenueText.DayMonthYear(parts[1]) ?? throw Missing(position, "a readable expiry"),
                            decimal.Parse(parts[2], CultureInfo.InvariantCulture),
                            parts[3].Equals("P", StringComparison.OrdinalIgnoreCase) ? OptionRight.Put : OptionRight.Call,
                            // The position's own "mark price" is the option's, not the coin's.
                            underlyingPrices is not null && underlyingPrices.TryGetValue(parts[0], out decimal coinPrice) && coinPrice > 0m ? coinPrice : throw Missing(position, "a price for the coin it is an option on"),
                            delta / quantity
                        )
                    );
                    break;
                }

                default:
                    throw new VenueException(VenueFailure.Unavailable, $"Bybit reported a position of an unknown kind, {category} ({position.Symbol}).");
            }
        }

        return new VenueSnapshot(asOf, result, balance.TotalEquity);
    }

    /// <summary>The sign comes from the entry's type, so it does not depend on how the venue signs the amount.</summary>
    public static IReadOnlyList<VenueTransfer> Transfers(IEnumerable<BybitTransactionLog> entries) =>
        entries
            .Where(e => e.Type is TransactionLogType.TransferIn or TransactionLogType.TransferOut && (e.Change ?? 0m) != 0m)
            .Select(e => new VenueTransfer(e.Id, new DateTimeOffset(DateTime.SpecifyKind(e.TransactionTime, DateTimeKind.Utc)), e.Asset.ToUpperInvariant(), (e.Type == TransactionLogType.TransferIn ? 1m : -1m) * Math.Abs(e.Change!.Value)))
            .ToList();

    private static string? Strip(string symbol, string[] suffixes)
    {
        string? suffix = suffixes.FirstOrDefault(s => symbol.EndsWith(s, StringComparison.OrdinalIgnoreCase) && symbol.Length > s.Length);
        return suffix is null ? null : symbol[..^suffix.Length];
    }

    /// <summary>"H26", "M26", "U26", "Z26": the last Friday of March, June, September or December.</summary>
    private static DateOnly? QuarterEnd(string code)
    {
        if (code.Length != 3 || !int.TryParse(code[1..], NumberStyles.None, CultureInfo.InvariantCulture, out int year))
        {
            return null;
        }

        int month = char.ToUpperInvariant(code[0]) switch
        {
            'H' => 3,
            'M' => 6,
            'U' => 9,
            'Z' => 12,
            _ => 0,
        };
        if (month == 0)
        {
            return null;
        }

        DateOnly day = new(2000 + year, month, DateTime.DaysInMonth(2000 + year, month));
        while (day.DayOfWeek != DayOfWeek.Friday)
        {
            day = day.AddDays(-1);
        }

        return day;
    }

    private static VenueException Missing(BybitPosition position, string what) => new(VenueFailure.Unavailable, $"Bybit reported {position.Symbol} without {what}.");
}
