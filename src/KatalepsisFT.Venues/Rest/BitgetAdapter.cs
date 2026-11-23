using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Domain.Instruments;
using KatalepsisFT.Infrastructure.Health;

namespace KatalepsisFT.Venues.Rest;

/// <summary>
/// Bitget over its v2 REST API: the spot account, USDT-margined futures, public prices, deposits and withdrawals.
/// Written new; Katalepsis has no Bitget client. Coin-margined and USDC futures, margin and Earn are not read.
/// Its keys are stored as "permissions unverified".
/// </summary>
public sealed class BitgetAdapter(HttpClient http, TimeProvider clock) : IVenueAdapter
{
    public const string HttpClientName = "venue:bitget";
    private const string BaseUrl = "https://api.bitget.com";
    private const string Futures = "productType=USDT-FUTURES";

    public Venue Venue => Venue.Bitget;

    public string DisplayName => "Bitget";

    public bool NeedsPassphrase => true;

    public string WorkerGroup => WorkerGroups.Cefi;

    public async Task<CredentialCheck> CheckCredentialsAsync(VenueCredentials credentials, CancellationToken cancellationToken)
    {
        await GetAsync(credentials, "/api/v2/spot/account/assets", string.Empty, cancellationToken);
        return new CredentialCheck(null, null);
    }

    public async Task<VenueSnapshot> GetSnapshotAsync(VenueCredentials credentials, CancellationToken cancellationToken)
    {
        JsonElement spot = await GetAsync(credentials, "/api/v2/spot/account/assets", string.Empty, cancellationToken);
        JsonElement tickers = await GetAsync(null, "/api/v2/spot/market/tickers", string.Empty, cancellationToken);
        JsonElement? accounts = null,
            positions = null;
        List<string> notes = [];
        try
        {
            accounts = await GetAsync(credentials, "/api/v2/mix/account/accounts", Futures, cancellationToken);
            positions = await GetAsync(credentials, "/api/v2/mix/position/all-position", Futures + "&marginCoin=USDT", cancellationToken);
        }
        catch (VenueException e)
        {
            // The spot call above proved the key is good, so this is a futures account that is missing or closed to the key.
            notes.Add($"The futures account was not read: {e.Message}");
            accounts = null;
            positions = null;
        }

        return BitgetMapper.Map(clock.GetUtcNow(), spot, tickers, accounts, positions) with { Notes = notes.Count == 0 ? null : notes };
    }

    public async Task<IReadOnlyList<VenueTransfer>> GetTransfersAsync(VenueCredentials credentials, DateTimeOffset since, CancellationToken cancellationToken)
    {
        string query = $"startTime={since.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}&endTime={clock.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}&limit=100";
        JsonElement deposits = await GetAsync(credentials, "/api/v2/spot/wallet/deposit-records", query, cancellationToken);
        JsonElement withdrawals = await GetAsync(credentials, "/api/v2/spot/wallet/withdrawal-records", query, cancellationToken);
        return BitgetMapper.Transfers(deposits, withdrawals);
    }

    /// <param name="credentials">Null for public data, which is not signed.</param>
    private async Task<JsonElement> GetAsync(VenueCredentials? credentials, string path, string query, CancellationToken cancellationToken)
    {
        string pathAndQuery = path + (query.Length == 0 ? string.Empty : "?" + query);
        using HttpRequestMessage request = new(HttpMethod.Get, BaseUrl + pathAndQuery);
        if (credentials is not null)
        {
            string timestamp = clock.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            request.Headers.TryAddWithoutValidation("ACCESS-KEY", credentials.ApiKey);
            request.Headers.TryAddWithoutValidation("ACCESS-SIGN", Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(credentials.ApiSecret), Encoding.UTF8.GetBytes(timestamp + "GET" + pathAndQuery))));
            request.Headers.TryAddWithoutValidation("ACCESS-TIMESTAMP", timestamp);
            request.Headers.TryAddWithoutValidation("ACCESS-PASSPHRASE", credentials.Passphrase ?? string.Empty);
            request.Headers.TryAddWithoutValidation("locale", "en-US");
        }

        (HttpStatusCode status, JsonElement? body, string _) = await RestVenue.SendAsync(http, request, DisplayName, path, cancellationToken);
        string code = body is JsonElement answer ? RestVenue.Text(answer, "code") : string.Empty;
        if (code == "00000" && body!.Value.TryGetProperty("data", out JsonElement data))
        {
            return data;
        }

        // 40006 invalid key, 40009 bad signature, 40012 wrong passphrase, 40014 no permission, 40018 address not allowed, 40037 key does not exist.
        bool rejected = status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || code is "40006" or "40009" or "40011" or "40012" or "40014" or "40018" or "40037";
        string message = body is JsonElement described ? RestVenue.Text(described, "msg") : string.Empty;
        throw new VenueException(rejected ? VenueFailure.Rejected : VenueFailure.Unavailable, $"{(message.Length > 0 ? RestVenue.Short(message) : "HTTP " + (int)status)}{(code.Length > 0 ? $" (code {code})" : string.Empty)} on {path}.");
    }
}

/// <summary>Bitget's answers in the shape every venue reports. Pure, so it is tested on written-out answers.</summary>
public static class BitgetMapper
{
    public static VenueSnapshot Map(DateTimeOffset asOf, JsonElement spot, JsonElement tickers, JsonElement? futuresAccounts, JsonElement? positions)
    {
        Dictionary<string, decimal> holdings = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement asset in spot.EnumerateArray())
        {
            RestVenue.Add(holdings, RestVenue.Text(asset, "coin"), (RestVenue.Number(asset, "available") ?? 0m) + (RestVenue.Number(asset, "frozen") ?? 0m) + (RestVenue.Number(asset, "locked") ?? 0m));
        }

        foreach (JsonElement account in futuresAccounts?.EnumerateArray() ?? default)
        {
            // The account's equity counts open profit; taking it out leaves the margin coin actually held.
            RestVenue.Add(holdings, RestVenue.Text(account, "marginCoin"), (RestVenue.Number(account, "accountEquity") ?? 0m) - (RestVenue.Number(account, "unrealizedPL") ?? 0m));
        }

        Dictionary<string, decimal> prices = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement ticker in tickers.EnumerateArray())
        {
            string symbol = RestVenue.Text(ticker, "symbol");
            if (symbol.EndsWith("USDT", StringComparison.Ordinal) && RestVenue.Number(ticker, "lastPr") is decimal last and > 0m)
            {
                prices[symbol[..^4]] = last;
            }
        }

        List<VenuePosition> result = RestVenue.Holdings("Bitget", holdings, asset => prices.TryGetValue(asset, out decimal price) ? price : null);
        foreach (JsonElement position in positions?.EnumerateArray() ?? default)
        {
            // "total" is the size in coins, always positive; the side says which way.
            decimal size = RestVenue.Number(position, "total") ?? 0m;
            if (size == 0m)
            {
                continue;
            }

            string symbol = RestVenue.Text(position, "symbol");
            if (!symbol.EndsWith("USDT", StringComparison.Ordinal) || symbol.Length <= 4)
            {
                throw new VenueException(VenueFailure.Unavailable, $"Bitget reported {symbol}, whose name this system cannot read.");
            }

            decimal mark = RestVenue.Number(position, "markPrice") is decimal m and > 0m ? m : throw new VenueException(VenueFailure.Unavailable, $"Bitget reported {symbol} without a mark price.");
            decimal? entry = RestVenue.Number(position, "openPriceAvg");
            int sign = RestVenue.Text(position, "holdSide").Equals("short", StringComparison.OrdinalIgnoreCase) ? -1 : 1;
            result.Add(new VenuePosition(InstrumentType.Perp, symbol[..^4].ToUpperInvariant(), sign * size, mark, entry is null or 0m ? null : entry, RestVenue.Number(position, "unrealizedPL")));
        }

        return new VenueSnapshot(asOf, result);
    }

    public static IReadOnlyList<VenueTransfer> Transfers(JsonElement deposits, JsonElement withdrawals) =>
        [
            .. Done(deposits).Select(d => new VenueTransfer("dep:" + RestVenue.Text(d, "orderId"), At(d), RestVenue.Text(d, "coin").ToUpperInvariant(), RestVenue.Number(d, "size") ?? 0m)),
            // The fee leaves the account with the withdrawal.
            .. Done(withdrawals).Select(w => new VenueTransfer("wd:" + RestVenue.Text(w, "orderId"), At(w), RestVenue.Text(w, "coin").ToUpperInvariant(), -((RestVenue.Number(w, "size") ?? 0m) + (RestVenue.Number(w, "fee") ?? 0m)))),
        ];

    private static IEnumerable<JsonElement> Done(JsonElement entries) =>
        entries.ValueKind == JsonValueKind.Array ? entries.EnumerateArray().Where(e => RestVenue.Text(e, "status").Equals("success", StringComparison.OrdinalIgnoreCase) && (RestVenue.Number(e, "size") ?? 0m) != 0m) : [];

    private static DateTimeOffset At(JsonElement entry) => DateTimeOffset.FromUnixTimeMilliseconds((long)(RestVenue.Number(entry, "cTime") ?? 0m));
}
