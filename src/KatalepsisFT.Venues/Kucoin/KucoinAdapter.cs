using System.Globalization;
using System.Net;
using CryptoExchange.Net.Objects;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Domain.Instruments;
using KatalepsisFT.Infrastructure.Health;
using Kucoin.Net;
using Kucoin.Net.Clients;
using Kucoin.Net.Enums;
using Kucoin.Net.Objects.Models.Futures;
using Kucoin.Net.Objects.Models.Spot;

namespace KatalepsisFT.Venues.Kucoin;

/// <summary>
/// KuCoin through Kucoin.Net, the library Katalepsis uses. Only read calls are used, those of Katalepsis'
/// Infrastructure/Services/Kucoin/KucoinApiClient.cs and KucoinFuturesApiClient.cs (commit 9847e4f): accounts, USD
/// prices, the futures account overview and positions, plus the key's own description. Futures margined in USDT and
/// in BTC are read; other margin coins are not.
/// </summary>
public sealed class KucoinAdapter(TimeProvider clock) : IVenueAdapter
{
    private static readonly string[] FuturesMarginAssets = ["USDT", "XBT"];

    public Venue Venue => Venue.Kucoin;

    public string DisplayName => "KuCoin";

    public bool NeedsPassphrase => true;

    public string WorkerGroup => WorkerGroups.Cefi;

    public async Task<CredentialCheck> CheckCredentialsAsync(VenueCredentials credentials, CancellationToken cancellationToken)
    {
        using KucoinRestClient client = Client(credentials);
        KucoinApiKey key = Data(await client.SpotApi.Account.GetApiKeyInfoAsync(cancellationToken), "key description");
        return KucoinMapper.Permissions(key.Permissions);
    }

    public async Task<VenueSnapshot> GetSnapshotAsync(VenueCredentials credentials, CancellationToken cancellationToken)
    {
        using KucoinRestClient client = Client(credentials);
        KucoinAccount[] accounts = Data(await client.SpotApi.Account.GetAccountsAsync(ct: cancellationToken), "accounts");
        Dictionary<string, decimal> prices = Data(await client.SpotApi.ExchangeData.GetFiatPricesAsync(ct: cancellationToken), "USD prices");

        List<KucoinAccountOverview> futures = [];
        List<string> notes = [];
        foreach (string asset in FuturesMarginAssets)
        {
            WebCallResult<KucoinAccountOverview> overview = await client.FuturesApi.Account.GetAccountOverviewAsync(asset, cancellationToken);
            if (overview.Success && overview.Data is not null)
            {
                futures.Add(overview.Data);
            }
            else if (!(VenueException.IsRefusal(overview.ResponseStatusCode) || (overview.ResponseStatusCode == System.Net.HttpStatusCode.OK && overview.Error?.Code is not null)))
            {
                // No answer, a busy venue or a server error: fail the read rather than drop the futures account from the book.
                Data(overview, $"{asset}-margined futures account");
            }
            else
            {
                // The venue said no, and the spot call above proved the key is good: a futures account that is missing or closed to the key.
                notes.Add($"The {asset}-margined futures account was not read: {overview.Error?.Message ?? "no answer"}.");
            }
        }

        KucoinPosition[] positions = futures.Count == 0 ? [] : Data(await client.FuturesApi.Account.GetPositionsAsync(ct: cancellationToken), "futures positions");
        // A position margined in another coin (ETH, USDC…) has its collateral in that coin's futures account. Counting
        // the position without it would leave the whole collateral out of NAV, so each such account is read too.
        foreach (string asset in positions.Where(p => p.CurrentQuantity != 0m && !string.IsNullOrEmpty(p.SettleAsset)).Select(p => p.SettleAsset!.ToUpperInvariant()).Distinct().Except(FuturesMarginAssets, StringComparer.OrdinalIgnoreCase).ToList())
        {
            futures.Add(Data(await client.FuturesApi.Account.GetAccountOverviewAsync(asset, cancellationToken), $"{asset}-margined futures account"));
        }

        return KucoinMapper.Map(clock.GetUtcNow(), accounts, prices, futures, positions) with { Notes = notes.Count == 0 ? null : notes };
    }

    public async Task<IReadOnlyList<VenueTransfer>> GetTransfersAsync(VenueCredentials credentials, DateTimeOffset since, CancellationToken cancellationToken)
    {
        using KucoinRestClient client = Client(credentials);
        var deposits = Data(await client.SpotApi.Account.GetDepositsAsync(startTime: since.UtcDateTime, status: DepositStatus.Success, pageSize: 100, ct: cancellationToken), "deposits");
        var withdrawals = Data(await client.SpotApi.Account.GetWithdrawalsAsync(startTime: since.UtcDateTime, status: WithdrawalStatus.Success, pageSize: 100, ct: cancellationToken), "withdrawals");
        return KucoinMapper.Transfers(deposits.Items, withdrawals.Items);
    }

    private static KucoinRestClient Client(VenueCredentials credentials) =>
        new(options =>
        {
            options.ApiCredentials = new KucoinCredentials(credentials.ApiKey, credentials.ApiSecret, credentials.Passphrase ?? string.Empty);
            options.RequestTimeout = TimeSpan.FromSeconds(20);
        });

    private static T Data<T>(WebCallResult<T> result, string what)
    {
        if (result.Success && result.Data is not null)
        {
            return result.Data;
        }

        // 400003 key not found, 400004 wrong passphrase, 400005 bad signature, 400006 address not allowed, 400007 no permission.
        bool rejected = result.ResponseStatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || result.Error?.Code is >= 400003 and <= 400007;
        string reason = result.Error is null ? "no answer" : $"{result.Error.Message} (code {result.Error.Code?.ToString(CultureInfo.InvariantCulture) ?? "none"})";
        throw new VenueException(rejected ? VenueFailure.Rejected : VenueFailure.Unavailable, $"{reason} reading the {what}.");
    }
}

/// <summary>KuCoin's answers in the shape every venue reports. Pure, so it is tested without the venue.</summary>
public static class KucoinMapper
{
    private static readonly string[] TradePermissions = ["Spot", "Margin", "Futures", "Earn", "Options", "Trade"];
    private static readonly string[] MoveMoneyPermissions = ["Withdrawal", "Withdraw", "Transfer", "FlexTransfers", "InnerTransfer"];
    private static readonly string[] ContractSuffixes = ["USDTM", "USDCM", "USDM"];

    /// <summary>"General" alone is a read-only key; every other entry lets the key do something.</summary>
    public static CredentialCheck Permissions(string? permissions)
    {
        string[] list = (permissions ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (list.Length == 0)
        {
            return new CredentialCheck(null, null);
        }

        bool Has(string[] names) => list.Any(p => names.Contains(p, StringComparer.OrdinalIgnoreCase));
        // Anything not known to be harmless is treated as able to trade, so a new permission cannot slip through.
        bool unknown = list.Any(p => !p.Equals("General", StringComparison.OrdinalIgnoreCase) && !TradePermissions.Contains(p, StringComparer.OrdinalIgnoreCase) && !MoveMoneyPermissions.Contains(p, StringComparer.OrdinalIgnoreCase));
        return new CredentialCheck(Has(TradePermissions) || unknown, Has(MoveMoneyPermissions));
    }

    public static VenueSnapshot Map(DateTimeOffset asOf, IEnumerable<KucoinAccount> accounts, IReadOnlyDictionary<string, decimal> usdPrices, IEnumerable<KucoinAccountOverview> futures, IEnumerable<KucoinPosition> positions)
    {
        // Main, trading and margin accounts and the futures wallets can hold the same asset; it is one line in the book.
        Dictionary<string, decimal> holdings = new(StringComparer.OrdinalIgnoreCase);
        foreach (KucoinAccount account in accounts)
        {
            holdings[account.Asset] = holdings.GetValueOrDefault(account.Asset) + account.Total;
        }

        foreach (KucoinAccountOverview overview in futures)
        {
            // The margin balance leaves out open profit, which arrives with the positions below.
            string asset = Coin(overview.Asset);
            holdings[asset] = holdings.GetValueOrDefault(asset) + overview.MarginBalance;
        }

        List<VenuePosition> result = [];
        foreach ((string asset, decimal quantity) in holdings.Where(h => h.Value != 0m).OrderBy(h => h.Key, StringComparer.Ordinal))
        {
            string symbol = asset.ToUpperInvariant();
            if (VenueText.IsCash(symbol))
            {
                result.Add(new VenuePosition(InstrumentType.Cash, symbol, quantity, 1m));
            }
            else
            {
                decimal price = usdPrices.TryGetValue(symbol, out decimal usd) && usd > 0m ? usd : throw new VenueException(VenueFailure.Unavailable, $"KuCoin holds {symbol} but gives no USD price for it.");
                result.Add(new VenuePosition(InstrumentType.Spot, symbol, quantity, price));
            }
        }

        foreach (KucoinPosition position in positions.Where(p => p.CurrentQuantity != 0m))
        {
            // "XBTUSDTM" and "ETHUSDCM" are perps settled in the stablecoin; "XBTUSDM" is settled in BTC.
            string? suffix = ContractSuffixes.FirstOrDefault(s => position.Symbol.EndsWith(s, StringComparison.OrdinalIgnoreCase) && position.Symbol.Length > s.Length);
            if (suffix is null)
            {
                throw new VenueException(VenueFailure.Unavailable, $"KuCoin reported {position.Symbol}, whose name this system cannot read.");
            }

            decimal mark = position.MarkPrice > 0m ? position.MarkPrice : throw new VenueException(VenueFailure.Unavailable, $"KuCoin reported {position.Symbol} without a mark price.");
            decimal value = Math.Abs(position.MarkValue);
            bool inverse = position.IsInverse;
            // Lots differ in size per contract. The position's value does not: USD for the first kind, coins for the second.
            decimal quantity = Math.Sign(position.CurrentQuantity) * (inverse ? value : value / mark);
            decimal unsettled = position.UnrealizedPnl * (inverse ? mark : 1m);
            result.Add(new VenuePosition(InstrumentType.Perp, Coin(position.Symbol[..^suffix.Length]), quantity, mark, position.AverageEntryPrice == 0m ? null : position.AverageEntryPrice, unsettled));
        }

        return new VenueSnapshot(asOf, result);
    }

    public static IReadOnlyList<VenueTransfer> Transfers(IEnumerable<KucoinDeposit> deposits, IEnumerable<KucoinWithdrawal> withdrawals) =>
        [
            // Moves between the user's own KuCoin accounts are marked inner and stay inside what is read.
            .. deposits.Where(d => d.Status == DepositStatus.Success && !d.IsInner).Select(d => new VenueTransfer($"dep:{d.WalletTransactionId}:{d.CreateTime.Ticks}", Utc(d.CreateTime), d.Asset.ToUpperInvariant(), d.Quantity)),
            .. withdrawals.Where(w => w.Status == WithdrawalStatus.Success && !w.IsInner).Select(w => new VenueTransfer("wd:" + w.Id, Utc(w.CreateTime), w.Asset.ToUpperInvariant(), -(w.Quantity + w.Fee))),
        ];

    /// <summary>KuCoin futures call bitcoin XBT.</summary>
    private static string Coin(string asset) => asset.Equals("XBT", StringComparison.OrdinalIgnoreCase) ? "BTC" : asset.ToUpperInvariant();

    private static DateTimeOffset Utc(DateTime time) => new(DateTime.SpecifyKind(time, DateTimeKind.Utc));
}
