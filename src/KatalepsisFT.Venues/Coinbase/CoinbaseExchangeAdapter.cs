using System.Text.Json;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Domain.Instruments;
using KatalepsisFT.Infrastructure.Health;

namespace KatalepsisFT.Venues.Coinbase;

/// <summary>
/// Coinbase Exchange: the account list, as in Katalepsis' Infrastructure/Services/Coinbase/Exchange/
/// CoinbaseExchangeApiClient.cs (commit 9847e4f), plus transfers, and Coinbase's public USD rates to value the
/// balances. Spot only. Coinbase does not say what a key may do, so its keys are stored as "permissions unverified".
/// </summary>
public sealed class CoinbaseExchangeAdapter(HttpClient http, TimeProvider clock) : IVenueAdapter
{
    public const string HttpClientName = "venue:coinbase-exchange";
    private const string RatesUrl = "https://api.coinbase.com/v2/exchange-rates?currency=USD";

    private readonly CoinbaseHttp _api = new(http, clock, "Coinbase Exchange", "https://api.exchange.coinbase.com", prime: false);

    public Venue Venue => Venue.CoinbaseExchange;

    public string DisplayName => "Coinbase Exchange";

    public bool NeedsPassphrase => true;

    public string WorkerGroup => WorkerGroups.Cefi;

    public async Task<CredentialCheck> CheckCredentialsAsync(VenueCredentials credentials, CancellationToken cancellationToken)
    {
        // The public clock first, so that a failure of the signed call below can be told apart from the venue being down.
        await _api.GetPublicAsync("https://api.exchange.coinbase.com/time", cancellationToken);
        try
        {
            await _api.GetAsync(credentials, "/accounts", null, cancellationToken);
        }
        catch (VenueException e) when (e.Failure == VenueFailure.Unavailable)
        {
            // Seen against the real venue: a key it does not know gets "Internal Server Error", not a refusal.
            throw new VenueException(VenueFailure.Rejected, $"{e.Message} Coinbase Exchange is up, and this is how it answers a key, secret or passphrase it does not recognise.", e);
        }

        return new CredentialCheck(null, null);
    }

    public async Task<VenueSnapshot> GetSnapshotAsync(VenueCredentials credentials, CancellationToken cancellationToken)
    {
        JsonElement accounts = await _api.GetAsync(credentials, "/accounts", null, cancellationToken);
        JsonElement rates = await _api.GetPublicAsync(RatesUrl, cancellationToken);
        return CoinbaseMapper.ExchangeSnapshot(clock.GetUtcNow(), accounts, rates);
    }

    public async Task<IReadOnlyList<VenueTransfer>> GetTransfersAsync(VenueCredentials credentials, DateTimeOffset since, CancellationToken cancellationToken)
    {
        JsonElement accounts = await _api.GetAsync(credentials, "/accounts", null, cancellationToken);
        // Newest first; a page of 100 is far more than arrives between two reads.
        JsonElement transfers = await _api.GetAsync(credentials, "/transfers", "limit=100", cancellationToken);
        return CoinbaseMapper.ExchangeTransfers(accounts, transfers, since);
    }
}

/// <summary>
/// Coinbase Prime: portfolios and their balances, as in Katalepsis' Infrastructure/Services/Coinbase/Prime/
/// CoinbasePrimeApiClient.cs (commit 9847e4f), plus deposits and withdrawals. Every portfolio the key can see is
/// read and added up. Prime states each balance's USD value itself.
/// </summary>
public sealed class CoinbasePrimeAdapter(HttpClient http, TimeProvider clock) : IVenueAdapter
{
    public const string HttpClientName = "venue:coinbase-prime";

    private readonly CoinbaseHttp _api = new(http, clock, "Coinbase Prime", "https://api.prime.coinbase.com", prime: true);

    public Venue Venue => Venue.CoinbasePrime;

    public string DisplayName => "Coinbase Prime";

    public bool NeedsPassphrase => true;

    public string WorkerGroup => WorkerGroups.Cefi;

    public async Task<CredentialCheck> CheckCredentialsAsync(VenueCredentials credentials, CancellationToken cancellationToken)
    {
        await PortfoliosAsync(credentials, cancellationToken);
        return new CredentialCheck(null, null);
    }

    public async Task<VenueSnapshot> GetSnapshotAsync(VenueCredentials credentials, CancellationToken cancellationToken)
    {
        List<JsonElement> balances = [];
        foreach (string portfolio in await PortfoliosAsync(credentials, cancellationToken))
        {
            balances.Add(await _api.GetAsync(credentials, $"/v1/portfolios/{portfolio}/balances", "balance_type=TOTAL_BALANCES", cancellationToken));
        }

        return CoinbaseMapper.PrimeSnapshot(clock.GetUtcNow(), balances);
    }

    public async Task<IReadOnlyList<VenueTransfer>> GetTransfersAsync(VenueCredentials credentials, DateTimeOffset since, CancellationToken cancellationToken)
    {
        List<VenueTransfer> transfers = [];
        string start = Uri.EscapeDataString(since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture));
        foreach (string portfolio in await PortfoliosAsync(credentials, cancellationToken))
        {
            JsonElement page = await _api.GetAsync(credentials, $"/v1/portfolios/{portfolio}/transactions", $"types=DEPOSIT&types=WITHDRAWAL&start_time={start}&limit=100", cancellationToken);
            transfers.AddRange(CoinbaseMapper.PrimeTransfers(page, since));
        }

        return transfers;
    }

    private async Task<List<string>> PortfoliosAsync(VenueCredentials credentials, CancellationToken cancellationToken)
    {
        JsonElement answer = await _api.GetAsync(credentials, "/v1/portfolios", null, cancellationToken);
        List<string> ids = answer.TryGetProperty("portfolios", out JsonElement list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().Select(p => CoinbaseHttp.Text(p, "id")).Where(id => id.Length > 0).ToList() : [];
        return ids.Count > 0 ? ids : throw new VenueException(VenueFailure.Rejected, "the key can see no portfolio.");
    }
}

/// <summary>Coinbase's answers in the shape every venue reports. Pure, so it is tested on written-out answers.</summary>
public static class CoinbaseMapper
{
    public static VenueSnapshot ExchangeSnapshot(DateTimeOffset asOf, JsonElement accounts, JsonElement usdRates)
    {
        JsonElement rates = usdRates.GetProperty("data").GetProperty("rates");
        Dictionary<string, decimal> holdings = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement account in accounts.EnumerateArray())
        {
            // "balance" includes what is on hold for open orders.
            string currency = CoinbaseHttp.Text(account, "currency");
            holdings[currency] = holdings.GetValueOrDefault(currency) + (CoinbaseHttp.Number(account, "balance") ?? 0m);
        }

        List<VenuePosition> result = [];
        foreach ((string currency, decimal quantity) in holdings.Where(h => h.Value != 0m).OrderBy(h => h.Key, StringComparer.Ordinal))
        {
            string symbol = currency.ToUpperInvariant();
            if (VenueText.IsCash(symbol))
            {
                result.Add(new VenuePosition(InstrumentType.Cash, symbol, quantity, 1m));
                continue;
            }

            // The rates say how much of each currency one dollar buys.
            decimal perDollar = CoinbaseHttp.Number(rates, symbol) ?? 0m;
            result.Add(new VenuePosition(InstrumentType.Spot, symbol, quantity, perDollar > 0m ? 1m / perDollar : throw new VenueException(VenueFailure.Unavailable, $"Coinbase holds {symbol} but gives no USD rate for it.")));
        }

        return new VenueSnapshot(asOf, result);
    }

    public static IReadOnlyList<VenueTransfer> ExchangeTransfers(JsonElement accounts, JsonElement transfers, DateTimeOffset since)
    {
        Dictionary<string, string> currencyByAccount = accounts.EnumerateArray().ToDictionary(a => CoinbaseHttp.Text(a, "id"), a => CoinbaseHttp.Text(a, "currency"));
        List<VenueTransfer> result = [];
        foreach (JsonElement transfer in transfers.EnumerateArray())
        {
            DateTimeOffset? completed = CoinbaseHttp.Time(transfer, "completed_at");
            string currency = CoinbaseHttp.Text(transfer, "currency") is { Length: > 0 } stated ? stated : currencyByAccount.GetValueOrDefault(CoinbaseHttp.Text(transfer, "account_id"), string.Empty);
            decimal amount = CoinbaseHttp.Number(transfer, "amount") ?? 0m;
            // Not completed, cancelled, or too old: no money has moved in the period asked about.
            if (completed is null || completed < since || CoinbaseHttp.Text(transfer, "canceled_at").Length > 0 || amount == 0m)
            {
                continue;
            }

            if (currency.Length == 0)
            {
                throw new VenueException(VenueFailure.Unavailable, "Coinbase reported a transfer without saying in which currency.");
            }

            // "deposit", "internal_deposit", "withdraw", "internal_withdraw".
            bool leaving = CoinbaseHttp.Text(transfer, "type").Contains("withdraw", StringComparison.OrdinalIgnoreCase);
            result.Add(new VenueTransfer(CoinbaseHttp.Text(transfer, "id"), completed.Value, currency.ToUpperInvariant(), leaving ? -Math.Abs(amount) : Math.Abs(amount)));
        }

        return result;
    }

    public static VenueSnapshot PrimeSnapshot(DateTimeOffset asOf, IEnumerable<JsonElement> portfolioBalances)
    {
        Dictionary<string, (decimal Quantity, decimal Usd)> holdings = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement portfolio in portfolioBalances)
        {
            foreach (JsonElement balance in portfolio.GetProperty("balances").EnumerateArray())
            {
                string symbol = CoinbaseHttp.Text(balance, "symbol").ToUpperInvariant();
                (decimal quantity, decimal usd) = holdings.GetValueOrDefault(symbol);
                holdings[symbol] = (quantity + (CoinbaseHttp.Number(balance, "amount") ?? 0m), usd + (CoinbaseHttp.Number(balance, "fiat_amount") ?? 0m));
            }
        }

        List<VenuePosition> result = [];
        foreach ((string symbol, (decimal quantity, decimal usd)) in holdings.Where(h => h.Value.Quantity != 0m).OrderBy(h => h.Key, StringComparer.Ordinal))
        {
            if (VenueText.IsCash(symbol))
            {
                result.Add(new VenuePosition(InstrumentType.Cash, symbol, quantity, 1m));
            }
            else
            {
                result.Add(new VenuePosition(InstrumentType.Spot, symbol, quantity, usd > 0m ? usd / quantity : throw new VenueException(VenueFailure.Unavailable, $"Coinbase Prime holds {symbol} but gives no USD value for it.")));
            }
        }

        return new VenueSnapshot(asOf, result, holdings.Values.Sum(h => h.Usd));
    }

    public static IReadOnlyList<VenueTransfer> PrimeTransfers(JsonElement page, DateTimeOffset since)
    {
        List<VenueTransfer> result = [];
        if (!page.TryGetProperty("transactions", out JsonElement transactions) || transactions.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (JsonElement transaction in transactions.EnumerateArray())
        {
            string type = CoinbaseHttp.Text(transaction, "type");
            string status = CoinbaseHttp.Text(transaction, "status");
            DateTimeOffset? at = CoinbaseHttp.Time(transaction, "completed_at") ?? CoinbaseHttp.Time(transaction, "created_at");
            decimal amount = Math.Abs(CoinbaseHttp.Number(transaction, "amount") ?? 0m);
            bool done = status is "TRANSACTION_DONE" or "TRANSACTION_IMPORTED";
            if (!done || at is null || at < since || amount == 0m || type is not ("DEPOSIT" or "WITHDRAWAL"))
            {
                continue;
            }

            result.Add(new VenueTransfer(CoinbaseHttp.Text(transaction, "id"), at.Value, CoinbaseHttp.Text(transaction, "symbol").ToUpperInvariant(), type == "WITHDRAWAL" ? -amount : amount));
        }

        return result;
    }
}
