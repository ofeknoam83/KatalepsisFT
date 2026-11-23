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
/// Gate over its v4 REST API: the spot account, USDT-settled futures, public prices, deposits and withdrawals.
/// Written new; Katalepsis has no Gate client. Margin, options, delivery futures and Earn are not read. Gate does
/// not say what a key may do, so its keys are stored as "permissions unverified".
/// </summary>
public sealed class GateAdapter(HttpClient http, TimeProvider clock) : IVenueAdapter
{
    public const string HttpClientName = "venue:gate";
    private const string Host = "https://api.gateio.ws";
    private const string Prefix = "/api/v4";

    public Venue Venue => Venue.Gate;

    public string DisplayName => "Gate";

    public bool NeedsPassphrase => false;

    public string WorkerGroup => WorkerGroups.Cefi;

    public async Task<CredentialCheck> CheckCredentialsAsync(VenueCredentials credentials, CancellationToken cancellationToken)
    {
        await GetAsync(credentials, "/spot/accounts", string.Empty, cancellationToken);
        return new CredentialCheck(null, null);
    }

    public async Task<VenueSnapshot> GetSnapshotAsync(VenueCredentials credentials, CancellationToken cancellationToken)
    {
        JsonElement spot = await GetAsync(credentials, "/spot/accounts", string.Empty, cancellationToken);
        JsonElement tickers = await GetAsync(null, "/spot/tickers", string.Empty, cancellationToken);
        JsonElement? futures = null,
            positions = null;
        List<string> notes = [];
        try
        {
            futures = await GetAsync(credentials, "/futures/usdt/accounts", string.Empty, cancellationToken);
            positions = await GetAsync(credentials, "/futures/usdt/positions", "holding=true", cancellationToken);
        }
        catch (VenueException e)
        {
            // The spot call above proved the key is good, so this is a futures account that is missing or closed to the key.
            notes.Add($"The futures account was not read: {e.Message}");
            futures = null;
            positions = null;
        }

        return GateMapper.Map(clock.GetUtcNow(), spot, tickers, futures, positions) with { Notes = notes.Count == 0 ? null : notes };
    }

    public async Task<IReadOnlyList<VenueTransfer>> GetTransfersAsync(VenueCredentials credentials, DateTimeOffset since, CancellationToken cancellationToken)
    {
        string query = $"from={since.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}&limit=100";
        JsonElement deposits = await GetAsync(credentials, "/wallet/deposits", query, cancellationToken);
        JsonElement withdrawals = await GetAsync(credentials, "/wallet/withdrawals", query, cancellationToken);
        return GateMapper.Transfers(deposits, withdrawals);
    }

    /// <param name="credentials">Null for public data, which is not signed.</param>
    private async Task<JsonElement> GetAsync(VenueCredentials? credentials, string path, string query, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, Host + Prefix + path + (query.Length == 0 ? string.Empty : "?" + query));
        if (credentials is not null)
        {
            string timestamp = clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            // Method, path, query, the hash of an empty body, and the time, each on its own line.
            string payload = string.Join('\n', "GET", Prefix + path, query, Convert.ToHexString(SHA512.HashData([])).ToLowerInvariant(), timestamp);
            request.Headers.TryAddWithoutValidation("KEY", credentials.ApiKey);
            request.Headers.TryAddWithoutValidation("Timestamp", timestamp);
            request.Headers.TryAddWithoutValidation("SIGN", Convert.ToHexString(HMACSHA512.HashData(Encoding.UTF8.GetBytes(credentials.ApiSecret), Encoding.UTF8.GetBytes(payload))).ToLowerInvariant());
        }

        (HttpStatusCode status, JsonElement? body, string _) = await RestVenue.SendAsync(http, request, DisplayName, path, cancellationToken);
        if ((int)status < 400 && body is JsonElement ok)
        {
            return ok;
        }

        string label = body is JsonElement error ? RestVenue.Text(error, "label") : string.Empty;
        string message = body is JsonElement described ? RestVenue.Text(described, "message") : string.Empty;
        bool rejected = status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || label is "INVALID_KEY" or "INVALID_SIGNATURE" or "FORBIDDEN" or "READ_ONLY" or "IP_FORBIDDEN";
        throw new VenueException(rejected ? VenueFailure.Rejected : VenueFailure.Unavailable, $"{(message.Length > 0 ? RestVenue.Short(message) : "HTTP " + (int)status)}{(label.Length > 0 ? $" ({label})" : string.Empty)} on {path}.");
    }
}

/// <summary>Gate's answers in the shape every venue reports. Pure, so it is tested on written-out answers.</summary>
public static class GateMapper
{
    public static VenueSnapshot Map(DateTimeOffset asOf, JsonElement spot, JsonElement tickers, JsonElement? futures, JsonElement? positions)
    {
        Dictionary<string, decimal> holdings = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement account in spot.EnumerateArray())
        {
            RestVenue.Add(holdings, RestVenue.Text(account, "currency"), (RestVenue.Number(account, "available") ?? 0m) + (RestVenue.Number(account, "locked") ?? 0m));
        }

        if (futures is JsonElement wallet)
        {
            // "total" is the wallet after settled profit; open profit arrives with the positions below.
            RestVenue.Add(holdings, RestVenue.Text(wallet, "currency") is { Length: > 0 } currency ? currency : "USDT", RestVenue.Number(wallet, "total") ?? 0m);
        }

        Dictionary<string, decimal> prices = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement ticker in tickers.EnumerateArray())
        {
            string pair = RestVenue.Text(ticker, "currency_pair");
            if (pair.EndsWith("_USDT", StringComparison.Ordinal) && RestVenue.Number(ticker, "last") is decimal last and > 0m)
            {
                prices[pair[..^5]] = last;
            }
        }

        List<VenuePosition> result = RestVenue.Holdings("Gate", holdings, asset => prices.TryGetValue(asset, out decimal price) ? price : null);
        foreach (JsonElement position in positions?.EnumerateArray() ?? default)
        {
            decimal size = RestVenue.Number(position, "size") ?? 0m;
            if (size == 0m)
            {
                continue;
            }

            string contract = RestVenue.Text(position, "contract");
            decimal mark = RestVenue.Number(position, "mark_price") is decimal m and > 0m ? m : throw new VenueException(VenueFailure.Unavailable, $"Gate reported {contract} without a mark price.");
            decimal value = Math.Abs(RestVenue.Number(position, "value") ?? throw new VenueException(VenueFailure.Unavailable, $"Gate reported {contract} without a value."));
            decimal? entry = RestVenue.Number(position, "entry_price");
            // Contracts differ in size per market; the position's value in USDT does not, so the size in coins comes from it.
            result.Add(new VenuePosition(InstrumentType.Perp, contract.Split('_')[0].ToUpperInvariant(), Math.Sign(size) * value / mark, mark, entry is null or 0m ? null : entry, RestVenue.Number(position, "unrealised_pnl")));
        }

        return new VenueSnapshot(asOf, result);
    }

    public static IReadOnlyList<VenueTransfer> Transfers(JsonElement deposits, JsonElement withdrawals) =>
        [
            .. Done(deposits).Select(d => new VenueTransfer("dep:" + RestVenue.Text(d, "id"), At(d), RestVenue.Text(d, "currency").ToUpperInvariant(), RestVenue.Number(d, "amount") ?? 0m)),
            // The fee leaves the account with the withdrawal.
            .. Done(withdrawals).Select(w => new VenueTransfer("wd:" + RestVenue.Text(w, "id"), At(w), RestVenue.Text(w, "currency").ToUpperInvariant(), -((RestVenue.Number(w, "amount") ?? 0m) + (RestVenue.Number(w, "fee") ?? 0m)))),
        ];

    private static IEnumerable<JsonElement> Done(JsonElement entries) => entries.EnumerateArray().Where(e => RestVenue.Text(e, "status") == "DONE" && (RestVenue.Number(e, "amount") ?? 0m) != 0m);

    private static DateTimeOffset At(JsonElement entry) => DateTimeOffset.FromUnixTimeSeconds((long)(RestVenue.Number(entry, "timestamp") ?? 0m));
}
