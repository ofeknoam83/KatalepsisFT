using System.Globalization;
using System.Net;
using CryptoExchange.Net.Objects;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Domain.Instruments;
using KatalepsisFT.Infrastructure.Health;
using Mexc.Net;
using Mexc.Net.Clients;
using Mexc.Net.Enums;
using Mexc.Net.Objects.Models.Futures;
using Mexc.Net.Objects.Models.Spot;

namespace KatalepsisFT.Venues.Mexc;

/// <summary>
/// MEXC through Mexc.Net, the library Katalepsis uses. Only read calls are used: the spot account and prices, as in
/// Katalepsis' Infrastructure/Services/Mexc/MexcApiClient.cs (commit 9847e4f), plus futures balances and positions.
/// MEXC does not say what a key may do, so its keys are stored as "permissions unverified".
/// </summary>
public sealed class MexcAdapter(TimeProvider clock) : IVenueAdapter
{
    public Venue Venue => Venue.Mexc;

    public string DisplayName => "MEXC";

    public bool NeedsPassphrase => false;

    public string WorkerGroup => WorkerGroups.Cefi;

    public async Task<CredentialCheck> CheckCredentialsAsync(VenueCredentials credentials, CancellationToken cancellationToken)
    {
        using MexcRestClient client = Client(credentials);
        // Proves the key works. The account's own trade and withdraw flags describe the account, not this key.
        Data(await client.SpotApi.Account.GetAccountInfoAsync(cancellationToken), "spot account");
        return new CredentialCheck(null, null);
    }

    public async Task<VenueSnapshot> GetSnapshotAsync(VenueCredentials credentials, CancellationToken cancellationToken)
    {
        using MexcRestClient client = Client(credentials);
        MexcAccountInfo spot = Data(await client.SpotApi.Account.GetAccountInfoAsync(cancellationToken), "spot account");
        Dictionary<string, decimal> prices = Data(await client.SpotApi.ExchangeData.GetPricesAsync(ct: cancellationToken), "spot prices").ToDictionary(p => p.Symbol, p => p.Price, StringComparer.OrdinalIgnoreCase);

        WebCallResult<MexcFuturesBalance[]> futuresBalances = await client.FuturesApi.Account.GetBalancesAsync(cancellationToken);
        if (!futuresBalances.Success || futuresBalances.Data is null)
        {
            // The spot call above proved the key is good. MEXC opens its futures API to few accounts.
            return MexcMapper.Map(clock.GetUtcNow(), spot.Balances, prices, [], [], new Dictionary<string, decimal>(), new Dictionary<string, decimal>()) with
            {
                Notes = [$"The futures account was not read: {futuresBalances.Error?.Message ?? "no answer"}."],
            };
        }

        MexcPosition[] positions = Data(await client.FuturesApi.Trading.GetPositionsAsync(ct: cancellationToken), "futures positions");
        Dictionary<string, decimal> contractSizes = [];
        Dictionary<string, decimal> marks = [];
        if (positions.Length > 0)
        {
            contractSizes = Data(await client.FuturesApi.ExchangeData.GetSymbolsAsync(ct: cancellationToken), "futures contracts").ToDictionary(c => c.Symbol, c => c.ContractSize, StringComparer.OrdinalIgnoreCase);
            marks = Data(await client.FuturesApi.ExchangeData.GetTickersAsync(cancellationToken), "futures prices").ToDictionary(t => t.Symbol, t => t.MarkPrice > 0m ? t.MarkPrice : t.LastPrice, StringComparer.OrdinalIgnoreCase);
        }

        return MexcMapper.Map(clock.GetUtcNow(), spot.Balances, prices, futuresBalances.Data, positions, contractSizes, marks);
    }

    public async Task<IReadOnlyList<VenueTransfer>> GetTransfersAsync(VenueCredentials credentials, DateTimeOffset since, CancellationToken cancellationToken)
    {
        using MexcRestClient client = Client(credentials);
        MexcDeposit[] deposits = Data(await client.SpotApi.Account.GetDepositHistoryAsync(startTime: since.UtcDateTime, ct: cancellationToken), "deposits");
        MexcWithdrawal[] withdrawals = Data(await client.SpotApi.Account.GetWithdrawHistoryAsync(startTime: since.UtcDateTime, ct: cancellationToken), "withdrawals");
        return MexcMapper.Transfers(deposits, withdrawals);
    }

    private static MexcRestClient Client(VenueCredentials credentials) =>
        new(options =>
        {
            options.ApiCredentials = new MexcCredentials(credentials.ApiKey, credentials.ApiSecret);
            options.RequestTimeout = TimeSpan.FromSeconds(20);
        });

    private static T Data<T>(WebCallResult<T> result, string what)
    {
        if (result.Success && result.Data is not null)
        {
            return result.Data;
        }

        // 10072 invalid key, 10073 invalid access key, 700002 bad signature, 700006 address not allowed, 700007 no permission.
        bool rejected = result.ResponseStatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || result.Error?.Code is 10072 or 10073 or 700002 or 700006 or 700007;
        string reason = result.Error is null ? "no answer" : $"{result.Error.Message} (code {result.Error.Code?.ToString(CultureInfo.InvariantCulture) ?? "none"})";
        throw new VenueException(rejected ? VenueFailure.Rejected : VenueFailure.Unavailable, $"{reason} reading the {what}.");
    }
}

/// <summary>MEXC's answers in the shape every venue reports. Pure, so it is tested without the venue.</summary>
public static class MexcMapper
{
    private static readonly string[] Quotes = ["USDT", "USDC"];

    public static VenueSnapshot Map(
        DateTimeOffset asOf,
        IEnumerable<MexcAccountBalance> spotBalances,
        IReadOnlyDictionary<string, decimal> spotPrices,
        IEnumerable<MexcFuturesBalance> futuresBalances,
        IEnumerable<MexcPosition> positions,
        IReadOnlyDictionary<string, decimal> contractSizes,
        IReadOnlyDictionary<string, decimal> markPrices
    )
    {
        // Spot and futures wallets can hold the same asset; it is one line in the book.
        Dictionary<string, decimal> holdings = new(StringComparer.OrdinalIgnoreCase);
        foreach (MexcAccountBalance balance in spotBalances)
        {
            holdings[balance.Asset] = holdings.GetValueOrDefault(balance.Asset) + balance.Total;
        }

        foreach (MexcFuturesBalance balance in futuresBalances)
        {
            // The cash balance leaves out open profit, which the positions below carry through their entry price.
            holdings[balance.Asset] = holdings.GetValueOrDefault(balance.Asset) + balance.CashBalance;
        }

        List<VenuePosition> result = [];
        foreach ((string asset, decimal quantity) in holdings.Where(h => h.Value != 0m).OrderBy(h => h.Key, StringComparer.Ordinal))
        {
            string symbol = asset.ToUpperInvariant();
            if (VenueText.IsCash(symbol))
            {
                result.Add(new VenuePosition(InstrumentType.Cash, symbol, quantity, 1m));
                continue;
            }

            string? quote = Quotes.FirstOrDefault(q => spotPrices.TryGetValue(symbol + q, out decimal p) && p > 0m);
            result.Add(new VenuePosition(InstrumentType.Spot, symbol, quantity, quote is null ? throw new VenueException(VenueFailure.Unavailable, $"MEXC holds {symbol} but lists no USD price for it.") : spotPrices[symbol + quote]));
        }

        foreach (MexcPosition position in positions.Where(p => p.PositionSize != 0m))
        {
            // "BTC_USDT": sized in contracts of a fixed number of coins. "BTC_USD" contracts are sized in USD and not read.
            string[] parts = position.Symbol.Split('_');
            if (parts.Length != 2 || !Quotes.Contains(parts[1], StringComparer.OrdinalIgnoreCase))
            {
                throw new VenueException(VenueFailure.Unavailable, $"MEXC reported {position.Symbol}, a kind of contract this system does not read.");
            }

            decimal size = contractSizes.TryGetValue(position.Symbol, out decimal contract) && contract > 0m ? contract : throw new VenueException(VenueFailure.Unavailable, $"MEXC gave no contract size for {position.Symbol}.");
            decimal mark = markPrices.TryGetValue(position.Symbol, out decimal price) && price > 0m ? price : throw new VenueException(VenueFailure.Unavailable, $"MEXC gave no price for {position.Symbol}.");
            // Sizes are always positive; the side says which way.
            decimal quantity = (position.PositionSide == PositionSide.Short ? -1m : 1m) * position.PositionSize * size;
            result.Add(new VenuePosition(InstrumentType.Perp, parts[0].ToUpperInvariant(), quantity, mark, position.HoldAveragePrice == 0m ? null : position.HoldAveragePrice));
        }

        return new VenueSnapshot(asOf, result);
    }

    public static IReadOnlyList<VenueTransfer> Transfers(IEnumerable<MexcDeposit> deposits, IEnumerable<MexcWithdrawal> withdrawals) =>
        [
            .. deposits.Where(d => d.Status == DepositStatus.Success).Select(d => new VenueTransfer("dep:" + d.TransactionId, Utc(d.InsertTime), Coin(d.Asset), d.Quantity)),
            .. withdrawals.Where(w => w.Status == WithdrawStatus.Success).Select(w => new VenueTransfer("wd:" + w.Id, Utc(w.ApplyTime), Coin(w.Asset), -(w.Quantity + w.TransactionFee))),
        ];

    /// <summary>Deposit records name the network too, as in "USDT-TRX".</summary>
    private static string Coin(string asset) => asset.Split('-')[0].ToUpperInvariant();

    private static DateTimeOffset Utc(DateTime time) => new(DateTime.SpecifyKind(time, DateTimeKind.Utc));
}
