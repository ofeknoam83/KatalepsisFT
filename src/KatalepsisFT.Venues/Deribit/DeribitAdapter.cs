using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Domain.Instruments;
using KatalepsisFT.Infrastructure.Health;

namespace KatalepsisFT.Venues.Deribit;

/// <summary>
/// Deribit over its REST API: sign in with the key, then account summaries and positions. Only the read calls of
/// Katalepsis' client were carried over (Infrastructure/Services/Deribit/DeribitApiClient.cs, commit 9847e4f:
/// public/auth, private/get_account_summaries, private/get_positions, public/get_index_price), extended from options
/// only to every kind of position.
/// </summary>
public sealed class DeribitAdapter(HttpClient http, TimeProvider clock) : IVenueAdapter
{
    public const string HttpClientName = "venue:deribit";
    public const string BaseUrl = "https://www.deribit.com/api/v2/";

    /// <summary>The newest entries per currency and kind; far more than arrive between two reads.</summary>
    private const int TransferPageSize = 50;

    public Venue Venue => Venue.Deribit;

    public string DisplayName => "Deribit";

    public bool NeedsPassphrase => false;

    public string WorkerGroup => WorkerGroups.Cefi;

    public async Task<CredentialCheck> CheckCredentialsAsync(VenueCredentials credentials, CancellationToken cancellationToken)
    {
        (_, string scope) = await SignInAsync(credentials, cancellationToken);
        HashSet<string> scopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Has(string area, string level) => scopes.Contains($"{area}:{level}");
        if (!(Has("account", "read") || Has("account", "read_write")) || !(Has("trade", "read") || Has("trade", "read_write")))
        {
            throw new VenueException(VenueFailure.Rejected, "the key needs the scopes account:read and trade:read to show balances and positions.");
        }

        return new CredentialCheck(Has("trade", "read_write") || Has("block_trade", "read_write"), Has("wallet", "read_write"));
    }

    public async Task<VenueSnapshot> GetSnapshotAsync(VenueCredentials credentials, CancellationToken cancellationToken)
    {
        (string token, _) = await SignInAsync(credentials, cancellationToken);
        JsonElement summaries = await GetAsync("private/get_account_summaries?extended=true", token, cancellationToken);
        JsonElement positions = await GetAsync("private/get_positions?currency=any", token, cancellationToken);

        List<(string Currency, decimal Balance, decimal Equity)> balances = [];
        foreach (JsonElement summary in summaries.GetProperty("summaries").EnumerateArray())
        {
            decimal balance = Number(summary, "balance") ?? 0m;
            decimal equity = Number(summary, "equity") ?? 0m;
            if (balance != 0m || equity != 0m)
            {
                balances.Add((summary.GetProperty("currency").GetString()!.ToUpperInvariant(), balance, equity));
            }
        }

        Dictionary<string, decimal> index = [];
        foreach (string currency in balances.Select(b => b.Currency).Where(c => !VenueText.IsCash(c)).Distinct())
        {
            JsonElement price = await GetAsync($"public/get_index_price?index_name={currency.ToLowerInvariant()}_usd", null, cancellationToken);
            index[currency] = Number(price, "index_price") ?? throw new VenueException(VenueFailure.Unavailable, $"Deribit gave no index price for {currency}.");
        }

        return DeribitMapper.Map(clock.GetUtcNow(), balances, index, positions);
    }

    /// <summary>Deposits, withdrawals and transfers between accounts, per currency. Needs the wallet:read scope.</summary>
    public async Task<IReadOnlyList<VenueTransfer>> GetTransfersAsync(VenueCredentials credentials, DateTimeOffset since, CancellationToken cancellationToken)
    {
        (string token, string scope) = await SignInAsync(credentials, cancellationToken);
        if (!scope.Contains("wallet:read", StringComparison.OrdinalIgnoreCase))
        {
            throw new VenueException(VenueFailure.Rejected, "the key lacks the wallet:read scope, so deposits and withdrawals cannot be seen and will look like profit or loss.");
        }

        JsonElement summaries = await GetAsync("private/get_account_summaries", token, cancellationToken);
        List<VenueTransfer> transfers = [];
        foreach (string currency in summaries.GetProperty("summaries").EnumerateArray().Select(s => s.GetProperty("currency").GetString()!.ToUpperInvariant()).Distinct())
        {
            foreach (string method in DeribitMapper.TransferMethods)
            {
                JsonElement page = await GetAsync($"private/{method}?currency={currency}&count={TransferPageSize}", token, cancellationToken);
                transfers.AddRange(DeribitMapper.Transfers(method, currency, page.GetProperty("data"), since));
            }
        }

        return transfers;
    }

    private async Task<(string Token, string Scope)> SignInAsync(VenueCredentials credentials, CancellationToken cancellationToken)
    {
        string query = $"public/auth?client_id={Uri.EscapeDataString(credentials.ApiKey)}&client_secret={Uri.EscapeDataString(credentials.ApiSecret)}&grant_type=client_credentials";
        JsonElement result = await GetAsync(query, null, cancellationToken);
        return (result.GetProperty("access_token").GetString()!, result.TryGetProperty("scope", out JsonElement scope) ? scope.GetString() ?? string.Empty : string.Empty);
    }

    /// <summary>The "result" of a call, or a <see cref="VenueException"/> saying why there is none. The secret never appears in a message.</summary>
    private async Task<JsonElement> GetAsync(string pathAndQuery, string? token, CancellationToken cancellationToken)
    {
        string method = pathAndQuery.Split('?')[0];
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(new Uri(BaseUrl), pathAndQuery));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        HttpResponseMessage response;
        string body;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new VenueException(VenueFailure.Unavailable, $"Deribit did not answer {method}.", e);
        }

        using (response)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(body);
            }
            catch (JsonException e)
            {
                throw new VenueException(VenueFailure.Unavailable, $"Deribit answered {method} with HTTP {(int)response.StatusCode} and no readable body.", e);
            }

            using (document)
            {
                if (document.RootElement.TryGetProperty("error", out JsonElement error))
                {
                    int code = error.TryGetProperty("code", out JsonElement c) && c.TryGetInt32(out int value) ? value : 0;
                    string message = error.TryGetProperty("message", out JsonElement m) ? m.GetString() ?? "error" : "error";
                    // 13004 invalid_credentials, 13009 unauthorized, 13021 forbidden, 13000-range: authorisation problems.
                    bool rejected = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || code is >= 13000 and < 13100;
                    throw new VenueException(rejected ? VenueFailure.Rejected : VenueFailure.Unavailable, $"{message} (code {code}) on {method}.");
                }

                if (!response.IsSuccessStatusCode || !document.RootElement.TryGetProperty("result", out JsonElement result))
                {
                    throw new VenueException(VenueFailure.Unavailable, $"Deribit answered {method} with HTTP {(int)response.StatusCode}.");
                }

                return result.Clone();
            }
        }
    }

    internal static decimal? Number(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetDecimal() : null;
}

/// <summary>Deribit's answers in the shape every venue reports. Pure, so it is tested on recorded answers.</summary>
public static class DeribitMapper
{
    public static VenueSnapshot Map(DateTimeOffset asOf, IReadOnlyList<(string Currency, decimal Balance, decimal Equity)> balances, IReadOnlyDictionary<string, decimal> indexPrices, JsonElement positions)
    {
        List<VenuePosition> result = [];
        decimal reportedEquity = 0m;
        foreach ((string currency, decimal balance, decimal equity) in balances)
        {
            reportedEquity += equity * (VenueText.IsCash(currency) ? 1m : indexPrices[currency]);
            if (balance == 0m)
            {
                continue;
            }

            // The balance is what has been paid in and settled; open profit and option value come from the positions.
            result.Add(VenueText.IsCash(currency) ? new VenuePosition(InstrumentType.Cash, currency, balance, 1m) : new VenuePosition(InstrumentType.Spot, currency, balance, indexPrices[currency]));
        }

        foreach (JsonElement position in positions.EnumerateArray())
        {
            decimal size = DeribitAdapter.Number(position, "size") ?? 0m;
            if (size == 0m)
            {
                continue;
            }

            string name = position.GetProperty("instrument_name").GetString()!;
            string kind = position.GetProperty("kind").GetString()!;
            string[] parts = name.Split('-');
            // "BTC-PERPETUAL" is margined and settled in BTC; "SOL_USDC-PERPETUAL" in USDC.
            bool linear = parts[0].Contains('_', StringComparison.Ordinal);
            string asset = parts[0].Split('_')[0].ToUpperInvariant();
            decimal mark = Required(position, "mark_price", name);
            decimal indexPrice = DeribitAdapter.Number(position, "index_price") ?? mark;
            decimal toUsd = linear ? 1m : indexPrice;
            decimal? entry = DeribitAdapter.Number(position, "average_price");

            switch (kind)
            {
                case "future":
                {
                    // An inverse future's size is in USD; its size in coins comes separately.
                    decimal quantity = DeribitAdapter.Number(position, "size_currency") ?? (linear ? size : size / mark);
                    bool perpetual = parts[^1].Equals("PERPETUAL", StringComparison.OrdinalIgnoreCase);
                    decimal? unsettled = DeribitAdapter.Number(position, "floating_profit_loss") is decimal floating ? floating * toUsd : null;
                    result.Add(new VenuePosition(perpetual ? InstrumentType.Perp : InstrumentType.Future, asset, quantity, mark, entry, unsettled, perpetual ? null : Expiry(parts[1], name)));
                    break;
                }

                case "option":
                {
                    if (parts.Length < 4)
                    {
                        throw new VenueException(VenueFailure.Unavailable, $"Deribit option {name} has a name this system cannot read.");
                    }

                    decimal positionDelta = Required(position, "delta", name);
                    result.Add(
                        new VenuePosition(
                            InstrumentType.Option,
                            asset,
                            size,
                            mark * toUsd,
                            entry * toUsd,
                            null,
                            Expiry(parts[1], name),
                            decimal.Parse(parts[2].Replace('d', '.'), CultureInfo.InvariantCulture),
                            parts[3].Equals("P", StringComparison.OrdinalIgnoreCase) ? OptionRight.Put : OptionRight.Call,
                            indexPrice,
                            positionDelta / size
                        )
                    );
                    break;
                }

                default:
                    // Spot holdings are in the balances; a combo is reported through its legs. Anything else is new to us.
                    if (kind is not ("spot" or "future_combo" or "option_combo"))
                    {
                        throw new VenueException(VenueFailure.Unavailable, $"Deribit reported a position of an unknown kind, {kind} ({name}).");
                    }

                    break;
            }
        }

        return new VenueSnapshot(asOf, result, Math.Round(reportedEquity, 2));
    }

    public static readonly string[] TransferMethods = ["get_deposits", "get_withdrawals", "get_transfers"];

    /// <summary>Completed entries of one of Deribit's three lists, signed: money in is positive.</summary>
    public static IEnumerable<VenueTransfer> Transfers(string method, string currency, JsonElement entries, DateTimeOffset since)
    {
        foreach (JsonElement entry in entries.EnumerateArray())
        {
            string state = entry.TryGetProperty("state", out JsonElement s) ? s.GetString() ?? string.Empty : string.Empty;
            decimal amount = DeribitAdapter.Number(entry, "amount") ?? 0m;
            (string id, string timeField, bool done, decimal signed) = method switch
            {
                "get_deposits" => ("dep:" + Text(entry, "transaction_id"), "received_timestamp", state == "completed", amount),
                // The fee leaves the account with the withdrawal.
                "get_withdrawals" => ("wd:" + Text(entry, "id"), "confirmed_timestamp", state == "completed", -(amount + (DeribitAdapter.Number(entry, "fee") ?? 0m))),
                // Between this account and another: "payment" is money sent, "income" money received.
                _ => ("tr:" + Text(entry, "id"), "updated_timestamp", state == "confirmed", Text(entry, "direction") == "payment" ? -amount : amount),
            };
            long milliseconds = entry.TryGetProperty(timeField, out JsonElement t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : entry.TryGetProperty("created_timestamp", out JsonElement c) && c.ValueKind == JsonValueKind.Number ? c.GetInt64() : 0;
            DateTimeOffset at = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
            if (done && at >= since)
            {
                yield return new VenueTransfer(id, at, currency, signed);
            }
        }
    }

    private static string Text(JsonElement entry, string property) =>
        entry.TryGetProperty(property, out JsonElement value) ? (value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText()) : string.Empty;

    private static decimal Required(JsonElement position, string property, string name) =>
        DeribitAdapter.Number(position, property) ?? throw new VenueException(VenueFailure.Unavailable, $"Deribit reported {name} without {property}.");

    private static DateOnly Expiry(string text, string name) => VenueText.DayMonthYear(text) ?? throw new VenueException(VenueFailure.Unavailable, $"Deribit instrument {name} has an expiry this system cannot read.");
}
