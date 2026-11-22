using System.Globalization;
using System.Net;
using CryptoExchange.Net.Objects;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Infrastructure.Health;
using OKX.Net;
using OKX.Net.Clients;
using OKX.Net.Enums;
using OKX.Net.Objects.Account;
using FtInstrumentType = KatalepsisFT.Domain.Instruments.InstrumentType;
using FtOptionRight = KatalepsisFT.Domain.Instruments.OptionRight;

namespace KatalepsisFT.Venues.Okx;

/// <summary>
/// OKX through OKX.Net, the library Katalepsis uses. Only its read calls are used, as in Katalepsis'
/// Infrastructure/Services/Okx/Perpetual/OKXPerpetualApiClient.cs (commit 9847e4f): account balance and positions,
/// plus the account configuration, which says what the key may do. Reads the trading account; the funding account is not included.
/// </summary>
public sealed class OkxAdapter(TimeProvider clock) : IVenueAdapter
{
    public Venue Venue => Venue.Okx;

    public string DisplayName => "OKX";

    public bool NeedsPassphrase => true;

    public string WorkerGroup => WorkerGroups.Cefi;

    public async Task<CredentialCheck> CheckCredentialsAsync(VenueCredentials credentials, CancellationToken cancellationToken)
    {
        using OKXRestClient client = Client(credentials);
        OKXAccountConfiguration configuration = Data(await client.UnifiedApi.Account.GetAccountConfigurationAsync(cancellationToken), "account configuration");
        string[] permissions = (configuration.Permissions ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (permissions.Length == 0)
        {
            return new CredentialCheck(null, null);
        }

        return new CredentialCheck(permissions.Contains("trade", StringComparer.OrdinalIgnoreCase), permissions.Contains("withdraw", StringComparer.OrdinalIgnoreCase));
    }

    public async Task<VenueSnapshot> GetSnapshotAsync(VenueCredentials credentials, CancellationToken cancellationToken)
    {
        using OKXRestClient client = Client(credentials);
        OKXAccountBalance balance = Data(await client.UnifiedApi.Account.GetAccountBalanceAsync(ct: cancellationToken), "account balance");
        OKXPosition[] positions = Data(await client.UnifiedApi.Account.GetPositionsAsync(ct: cancellationToken), "positions");
        return OkxMapper.Map(clock.GetUtcNow(), balance, positions);
    }

    /// <summary>Transfers between the trading account, which is what is read, and anywhere else. OKX keeps seven days of these.</summary>
    public async Task<IReadOnlyList<VenueTransfer>> GetTransfersAsync(VenueCredentials credentials, DateTimeOffset since, CancellationToken cancellationToken)
    {
        using OKXRestClient client = Client(credentials);
        OKXAccountBill[] bills = Data(await client.UnifiedApi.Account.GetBillHistoryAsync(billType: AccountBillType.Transfer, startTime: since.UtcDateTime, ct: cancellationToken), "transfers");
        return OkxMapper.Transfers(bills);
    }

    private static OKXRestClient Client(VenueCredentials credentials) =>
        new(options =>
        {
            options.ApiCredentials = new OKXCredentials(credentials.ApiKey, credentials.ApiSecret, credentials.Passphrase ?? string.Empty);
            options.RequestTimeout = TimeSpan.FromSeconds(20);
        });

    private static T Data<T>(WebCallResult<T> result, string what)
    {
        if (result.Success && result.Data is not null)
        {
            return result.Data;
        }

        bool rejected = result.ResponseStatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
        string reason = result.Error is null ? "no answer" : $"{result.Error.Message} (code {result.Error.Code?.ToString(CultureInfo.InvariantCulture) ?? "none"})";
        throw new VenueException(rejected ? VenueFailure.Rejected : VenueFailure.Unavailable, $"{reason} reading the {what}.");
    }
}

/// <summary>OKX's answers in the shape every venue reports. Pure, so it is tested without the venue.</summary>
public static class OkxMapper
{
    public static VenueSnapshot Map(DateTimeOffset asOf, OKXAccountBalance balance, IReadOnlyList<OKXPosition> positions)
    {
        List<VenuePosition> result = [];
        foreach (OKXAccountBalanceDetail detail in balance.Details ?? [])
        {
            // The cash balance leaves out open profit, which arrives with the positions below.
            decimal cash = detail.CashBalance ?? 0m;
            if (cash == 0m)
            {
                continue;
            }

            string asset = detail.Asset.ToUpperInvariant();
            decimal equity = detail.Equity ?? 0m;
            decimal price = equity != 0m && detail.UsdEquity is decimal usd ? usd / equity : VenueText.IsCash(asset) ? 1m : throw new VenueException(VenueFailure.Unavailable, $"OKX gave no USD value for the {asset} balance.");
            result.Add(new VenuePosition(VenueText.IsCash(asset) ? FtInstrumentType.Cash : FtInstrumentType.Spot, asset, cash, price));
        }

        foreach (OKXPosition position in positions)
        {
            decimal contracts = position.PositionsQuantity ?? 0m;
            // A margin position is a loan, already in the balances as cash and liabilities.
            if (contracts == 0m || position.InstrumentType == InstrumentType.Margin)
            {
                continue;
            }

            string[] parts = position.Symbol.Split('-');
            string asset = parts[0].ToUpperInvariant();
            // "BTC-USDT-SWAP" is margined and settled in USDT; "BTC-USD-SWAP" in BTC.
            bool linear = parts.Length > 1 && !parts[1].Equals("USD", StringComparison.OrdinalIgnoreCase);
            // In hedge mode each side is its own position with a positive quantity; in net mode the sign is in the quantity.
            int sign = position.PositionSide == PositionSide.Short ? -1 : position.PositionSide == PositionSide.Long ? 1 : Math.Sign(contracts);
            decimal mark = position.MarkPrice ?? throw Missing(position, "a mark price");
            decimal notional = Math.Abs(position.NotionalUsd ?? throw Missing(position, "a notional value"));

            switch (position.InstrumentType)
            {
                case InstrumentType.Swap:
                case InstrumentType.Futures:
                {
                    // Contracts differ in size per instrument; the notional value does not, so quantity in coins comes from it.
                    decimal quantity = sign * notional / mark;
                    decimal? unsettled = position.UnrealizedProfitAndLoss is decimal upl ? upl * (linear ? 1m : mark) : null;
                    bool perpetual = position.InstrumentType == InstrumentType.Swap;
                    DateOnly? expiry = perpetual ? null : VenueText.YearMonthDay(parts[^1]) ?? throw Missing(position, "a readable expiry");
                    result.Add(new VenuePosition(perpetual ? FtInstrumentType.Perp : FtInstrumentType.Future, asset, quantity, mark, position.AveragePrice, unsettled, expiry));
                    break;
                }

                case InstrumentType.Option:
                {
                    if (parts.Length < 5)
                    {
                        throw Missing(position, "a readable name");
                    }

                    decimal underlying = position.IndexPrice ?? throw Missing(position, "an index price");
                    decimal quantity = sign * notional / underlying;
                    // Premiums are quoted in the coin for coin-settled options.
                    decimal toUsd = linear ? 1m : underlying;
                    decimal delta = position.DeltaBS ?? throw Missing(position, "a delta");
                    result.Add(
                        new VenuePosition(
                            FtInstrumentType.Option,
                            asset,
                            quantity,
                            mark * toUsd,
                            position.AveragePrice * toUsd,
                            null,
                            VenueText.YearMonthDay(parts[2]) ?? throw Missing(position, "a readable expiry"),
                            decimal.Parse(parts[3], CultureInfo.InvariantCulture),
                            parts[4].Equals("P", StringComparison.OrdinalIgnoreCase) ? FtOptionRight.Put : FtOptionRight.Call,
                            underlying,
                            delta / quantity
                        )
                    );
                    break;
                }

                default:
                    throw new VenueException(VenueFailure.Unavailable, $"OKX reported a position of an unknown kind, {position.InstrumentType} ({position.Symbol}).");
            }
        }

        return new VenueSnapshot(asOf, result, balance.TotalEquity);
    }

    /// <summary>A transfer bill's balance change is already signed: positive into the trading account.</summary>
    public static IReadOnlyList<VenueTransfer> Transfers(IEnumerable<OKXAccountBill> bills) =>
        bills.Where(b => (b.BalanceChange ?? 0m) != 0m).Select(b => new VenueTransfer(Convert.ToString(b.BillId, CultureInfo.InvariantCulture)!, new DateTimeOffset(DateTime.SpecifyKind(b.Time, DateTimeKind.Utc)), b.Asset.ToUpperInvariant(), b.BalanceChange!.Value)).ToList();

    private static VenueException Missing(OKXPosition position, string what) => new(VenueFailure.Unavailable, $"OKX reported {position.Symbol} without {what}.");
}
