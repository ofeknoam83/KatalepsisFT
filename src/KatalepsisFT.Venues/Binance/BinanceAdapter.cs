using System.Globalization;
using System.Net;
using Binance.Net;
using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using Binance.Net.Objects.Models.Spot;
using CryptoExchange.Net.Objects;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Domain.Instruments;
using KatalepsisFT.Infrastructure.Health;

namespace KatalepsisFT.Venues.Binance;

/// <summary>
/// Binance through Binance.Net, the library Katalepsis uses. Only read calls are used, those of Katalepsis'
/// Infrastructure/Services/Binance/BinanceApiClient.cs and BinanceUsdFuturesApiClient.cs (commit 9847e4f): the spot
/// account, spot prices, USDⓈ-M futures balances and positions, plus the key's permissions. Not read: coin-margined
/// futures, margin and options accounts, the funding wallet and Earn products other than flexible savings.
/// </summary>
public sealed class BinanceAdapter(TimeProvider clock) : IVenueAdapter
{
    /// <summary>Binance's answer when a key may not use an endpoint, which is also what an account without futures gives.</summary>
    private const int NoPermission = -2015;

    public Venue Venue => Venue.Binance;

    public string DisplayName => "Binance";

    public bool NeedsPassphrase => false;

    public string WorkerGroup => WorkerGroups.Cefi;

    public async Task<CredentialCheck> CheckCredentialsAsync(VenueCredentials credentials, CancellationToken cancellationToken)
    {
        using BinanceRestClient client = Client(credentials);
        BinanceAPIKeyPermissions permissions = Data(await client.SpotApi.Account.GetAPIKeyPermissionsAsync(ct: cancellationToken), "key permissions");
        if (!permissions.EnableReading)
        {
            throw new VenueException(VenueFailure.Rejected, "the key is not allowed to read the account.");
        }

        bool canTrade = permissions.EnableSpotAndMarginTrading || permissions.EnableFutures || permissions.EnableMargin || permissions.EnableVanillaOptions || permissions.EnablePortfolioMarginTrading || permissions.EnableFixApiTrade;
        // Transfers to other accounts move money out just as a withdrawal does.
        bool canWithdraw = permissions.EnableWithdrawals || permissions.EnableInternalTransfer || permissions.PermitsUniversalTransfer;
        return new CredentialCheck(canTrade, canWithdraw);
    }

    public async Task<VenueSnapshot> GetSnapshotAsync(VenueCredentials credentials, CancellationToken cancellationToken)
    {
        using BinanceRestClient client = Client(credentials);
        BinanceAccountInfo spot = Data(await client.SpotApi.Account.GetAccountInfoAsync(omitZeroBalances: true, ct: cancellationToken), "spot account");
        Dictionary<string, decimal> prices = Data(await client.SpotApi.ExchangeData.GetPricesAsync(ct: cancellationToken), "spot prices").ToDictionary(p => p.Symbol, p => p.Price, StringComparer.OrdinalIgnoreCase);

        WebCallResult<BinanceUsdFuturesAccountBalance[]> futuresBalances = await client.UsdFuturesApi.Account.GetBalancesAsync(ct: cancellationToken);
        if (!futuresBalances.Success && futuresBalances.Error?.Code == NoPermission)
        {
            // The spot call above proved the key itself is good, so this is an account without futures.
            return BinanceMapper.Map(clock.GetUtcNow(), spot.Balances, prices, [], []) with { Notes = ["The futures account was not read: Binance says this key may not see it, or there is none."] };
        }

        BinancePositionDetailsUsdt[] positions = Data(await client.UsdFuturesApi.Account.GetPositionInformationAsync(ct: cancellationToken), "futures positions");
        return BinanceMapper.Map(clock.GetUtcNow(), spot.Balances, prices, Data(futuresBalances, "futures balances"), positions);
    }

    /// <summary>
    /// Deposits to and withdrawals from the account. Moves between the spot and futures wallets stay inside what is
    /// read and are not flows.
    /// </summary>
    public async Task<IReadOnlyList<VenueTransfer>> GetTransfersAsync(VenueCredentials credentials, DateTimeOffset since, CancellationToken cancellationToken)
    {
        using BinanceRestClient client = Client(credentials);
        BinanceDeposit[] deposits = Data(await client.SpotApi.Account.GetDepositHistoryAsync(startTime: since.UtcDateTime, ct: cancellationToken), "deposits");
        BinanceWithdrawal[] withdrawals = Data(await client.SpotApi.Account.GetWithdrawalHistoryAsync(startTime: since.UtcDateTime, ct: cancellationToken), "withdrawals");
        return BinanceMapper.Transfers(deposits, withdrawals);
    }

    private static BinanceRestClient Client(VenueCredentials credentials) =>
        new(options =>
        {
            options.ApiCredentials = new BinanceCredentials(credentials.ApiKey, credentials.ApiSecret);
            options.RequestTimeout = TimeSpan.FromSeconds(20);
        });

    private static T Data<T>(WebCallResult<T> result, string what)
    {
        if (result.Success && result.Data is not null)
        {
            return result.Data;
        }

        // −2014 malformed key, −2015 invalid key, address or permission, −1022 bad signature.
        bool rejected = result.ResponseStatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || result.Error?.Code is -2014 or -2015 or -1022;
        string reason = result.Error is null ? "no answer" : $"{result.Error.Message} (code {result.Error.Code?.ToString(CultureInfo.InvariantCulture) ?? "none"})";
        throw new VenueException(rejected ? VenueFailure.Rejected : VenueFailure.Unavailable, $"{reason} reading the {what}.");
    }
}

/// <summary>Binance's answers in the shape every venue reports. Pure, so it is tested without the venue.</summary>
public static class BinanceMapper
{
    private static readonly string[] Quotes = ["USDT", "USDC", "FDUSD", "BUSD"];

    /// <summary>Contracts quoted per thousand or million coins, such as 1000PEPEUSDT.</summary>
    private static readonly (string Prefix, decimal Units)[] Multiples = [("1000000", 1_000_000m), ("10000", 10_000m), ("1000", 1_000m)];

    public static VenueSnapshot Map(
        DateTimeOffset asOf,
        IEnumerable<BinanceBalance> spotBalances,
        IReadOnlyDictionary<string, decimal> spotPrices,
        IEnumerable<BinanceUsdFuturesAccountBalance> futuresBalances,
        IEnumerable<BinancePositionDetailsUsdt> futuresPositions
    )
    {
        // Spot and futures wallets can hold the same asset; it is one line in the book.
        Dictionary<string, decimal> holdings = new(StringComparer.OrdinalIgnoreCase);
        foreach (BinanceBalance balance in spotBalances)
        {
            // "LDBTC" is BTC lent out in flexible savings: still the account's BTC.
            string asset = balance.Asset.StartsWith("LD", StringComparison.Ordinal) && balance.Asset.Length > 4 ? balance.Asset[2..] : balance.Asset;
            holdings[asset] = holdings.GetValueOrDefault(asset) + balance.Total;
        }

        foreach (BinanceUsdFuturesAccountBalance balance in futuresBalances)
        {
            // The wallet balance leaves out open profit, which arrives with the positions below.
            holdings[balance.Asset] = holdings.GetValueOrDefault(balance.Asset) + balance.WalletBalance;
        }

        List<VenuePosition> result = [];
        foreach ((string asset, decimal quantity) in holdings.Where(h => h.Value != 0m).OrderBy(h => h.Key, StringComparer.Ordinal))
        {
            string symbol = asset.ToUpperInvariant();
            result.Add(VenueText.IsCash(symbol) ? new VenuePosition(InstrumentType.Cash, symbol, quantity, 1m) : new VenuePosition(InstrumentType.Spot, symbol, quantity, SpotPrice(symbol, spotPrices)));
        }

        foreach (BinancePositionDetailsUsdt position in futuresPositions.Where(p => p.Quantity != 0m))
        {
            // "BTCUSDT" is the perp; "BTCUSDT_261225" a dated future.
            string[] parts = position.Symbol.Split('_');
            string? quote = Quotes.FirstOrDefault(q => parts[0].EndsWith(q, StringComparison.OrdinalIgnoreCase));
            if (quote is null)
            {
                throw new VenueException(VenueFailure.Unavailable, $"Binance reported {position.Symbol}, whose name this system cannot read.");
            }

            string asset = parts[0][..^quote.Length].ToUpperInvariant();
            decimal units = 1m;
            foreach ((string prefix, decimal multiple) in Multiples)
            {
                if (asset.StartsWith(prefix, StringComparison.Ordinal) && asset.Length > prefix.Length && char.IsLetter(asset[prefix.Length]))
                {
                    asset = asset[prefix.Length..];
                    units = multiple;
                    break;
                }
            }

            DateOnly? expiry = parts.Length > 1 ? VenueText.YearMonthDay(parts[1]) ?? throw new VenueException(VenueFailure.Unavailable, $"Binance reported {position.Symbol} with an expiry this system cannot read.") : null;
            // The quantity is already signed: a short is negative in both one-way and hedge mode.
            result.Add(
                new VenuePosition(expiry is null ? InstrumentType.Perp : InstrumentType.Future, asset, position.Quantity * units, position.MarkPrice / units, position.EntryPrice == 0m ? null : position.EntryPrice / units, position.UnrealizedPnl, expiry)
            );
        }

        return new VenueSnapshot(asOf, result);
    }

    public static IReadOnlyList<VenueTransfer> Transfers(IEnumerable<BinanceDeposit> deposits, IEnumerable<BinanceWithdrawal> withdrawals) =>
        [
            .. deposits.Where(d => d.Status == DepositStatus.Success).Select(d => new VenueTransfer("dep:" + d.Id, Utc(d.CompleteTime ?? d.InsertTime), d.Asset.ToUpperInvariant(), d.Quantity)),
            // The fee leaves the account with the withdrawal.
            .. withdrawals.Where(w => w.Status == WithdrawalStatus.Completed).Select(w => new VenueTransfer("wd:" + w.Id, Utc(w.CompleteTime ?? w.ApplyTime), w.Asset.ToUpperInvariant(), -(w.Quantity + w.TransactionFee))),
        ];

    private static DateTimeOffset Utc(DateTime time) => new(DateTime.SpecifyKind(time, DateTimeKind.Utc));

    private static decimal SpotPrice(string asset, IReadOnlyDictionary<string, decimal> prices)
    {
        foreach (string quote in Quotes)
        {
            if (prices.TryGetValue(asset + quote, out decimal price) && price > 0m)
            {
                return price;
            }
        }

        // Left out silently, the holding would simply vanish from NAV.
        throw new VenueException(VenueFailure.Unavailable, $"Binance holds {asset} but lists no USD price for it.");
    }
}
