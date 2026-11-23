using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Infrastructure.Health;

namespace KatalepsisFT.Venues.Rest;

/// <summary>
/// Kraken's spot account over its REST API: balances, public prices, and the ledger of deposits and withdrawals.
/// Written new; Katalepsis has no Kraken client. Kraken Futures is a separate system with its own keys and is not
/// read. Kraken does not say what a key may do, so its keys are stored as "permissions unverified".
/// </summary>
public sealed class KrakenAdapter(HttpClient http, TimeProvider clock) : IVenueAdapter
{
    public const string HttpClientName = "venue:kraken";
    private const string BaseUrl = "https://api.kraken.com";

    public Venue Venue => Venue.Kraken;

    public string DisplayName => "Kraken";

    public bool NeedsPassphrase => false;

    public string WorkerGroup => WorkerGroups.Cefi;

    /// <summary>Kraken counts private calls per key and lets the count fall by one every three seconds on a starter account.</summary>
    public TimeSpan MinReadInterval => TimeSpan.FromSeconds(6);

    public async Task<CredentialCheck> CheckCredentialsAsync(VenueCredentials credentials, CancellationToken cancellationToken)
    {
        await PrivateAsync(credentials, "/0/private/Balance", [], cancellationToken);
        try
        {
            return Permissions(await PrivateAsync(credentials, "/0/private/GetApiKeyInfo", [], cancellationToken));
        }
        catch (VenueException)
        {
            // The key works (the balance was read); what it may do could not be asked.
            return new CredentialCheck(null, null);
        }
    }

    /// <summary>
    /// What the key may do, from Kraken's own description of it: "modify-trades" and "close-trades" are trading,
    /// "withdraw-funds" and the two about withdrawal addresses are taking money out.
    /// </summary>
    public static CredentialCheck Permissions(JsonElement info)
    {
        if (info.ValueKind != JsonValueKind.Object || !info.TryGetProperty("permissions", out JsonElement permissions) || permissions.ValueKind != JsonValueKind.Array)
        {
            return new CredentialCheck(null, null);
        }

        List<string> given = permissions.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.ToLowerInvariant()).ToList();
        string[] harmless = ["query-funds", "query-open-trades", "query-closed-trades", "query-ledger", "export-data", "create-ws-token"];
        string[] moving = ["withdraw-funds", "add-withdraw-address", "update-withdraw-address"];
        // Anything not known to be harmless is treated as able to trade, so a new permission cannot slip through.
        return new CredentialCheck(given.Any(g => !harmless.Contains(g) && !moving.Contains(g)), given.Any(g => moving.Contains(g)));
    }

    public async Task<VenueSnapshot> GetSnapshotAsync(VenueCredentials credentials, CancellationToken cancellationToken)
    {
        JsonElement balances = await PrivateAsync(credentials, "/0/private/Balance", [], cancellationToken);
        using HttpRequestMessage request = new(HttpMethod.Get, BaseUrl + "/0/public/Ticker");
        JsonElement tickers = Result(await RestVenue.SendAsync(http, request, DisplayName, "prices", cancellationToken), "prices");
        return KrakenMapper.Map(clock.GetUtcNow(), balances, tickers);
    }

    public async Task<IReadOnlyList<VenueTransfer>> GetTransfersAsync(VenueCredentials credentials, DateTimeOffset since, CancellationToken cancellationToken)
    {
        List<VenueTransfer> transfers = [];
        foreach (string type in new[] { "deposit", "withdrawal", "transfer" })
        {
            JsonElement page = await PrivateAsync(credentials, "/0/private/Ledgers", [("type", type), ("start", since.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))], cancellationToken);
            transfers.AddRange(KrakenMapper.Transfers(page));
        }

        return transfers;
    }

    private async Task<JsonElement> PrivateAsync(VenueCredentials credentials, string path, (string Name, string Value)[] parameters, CancellationToken cancellationToken)
    {
        // Kraken wants a number that only ever goes up for a key; the clock in milliseconds does.
        string nonce = clock.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        string post = string.Join('&', new[] { ("nonce", nonce) }.Concat(parameters).Select(p => $"{p.Item1}={Uri.EscapeDataString(p.Item2)}"));
        string signature;
        try
        {
            byte[] message = [.. Encoding.UTF8.GetBytes(path), .. SHA256.HashData(Encoding.UTF8.GetBytes(nonce + post))];
            signature = Convert.ToBase64String(HMACSHA512.HashData(Convert.FromBase64String(credentials.ApiSecret), message));
        }
        catch (FormatException)
        {
            throw new VenueException(VenueFailure.Rejected, "the API secret is not in the form Kraken issues.");
        }

        using HttpRequestMessage request = new(HttpMethod.Post, BaseUrl + path) { Content = new StringContent(post, Encoding.UTF8, "application/x-www-form-urlencoded") };
        request.Headers.TryAddWithoutValidation("API-Key", credentials.ApiKey);
        request.Headers.TryAddWithoutValidation("API-Sign", signature);
        string what = path.Split('/')[^1];
        return Result(await RestVenue.SendAsync(http, request, DisplayName, what, cancellationToken), what);
    }

    /// <summary>Kraken answers 200 with an "error" list; "EAPI" and permission errors are about the key.</summary>
    private static JsonElement Result((HttpStatusCode Status, JsonElement? Body, string Raw) answer, string what)
    {
        if (answer.Body is JsonElement body && body.TryGetProperty("error", out JsonElement errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
        {
            string message = string.Join("; ", errors.EnumerateArray().Select(e => e.GetString()));
            bool rateLimit = message.Contains("Rate limit", StringComparison.OrdinalIgnoreCase);
            // A nonce out of order is about timing (the same key in use elsewhere), not about the key being wrong, and passes on a later try.
            bool nonce = message.Contains("nonce", StringComparison.OrdinalIgnoreCase);
            bool aboutKey = !rateLimit && !nonce && (message.Contains("EAPI:", StringComparison.Ordinal) || message.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) || message.Contains("Invalid key", StringComparison.OrdinalIgnoreCase) || message.Contains("Invalid signature", StringComparison.OrdinalIgnoreCase));
            throw new VenueException(aboutKey ? VenueFailure.Rejected : VenueFailure.Unavailable, $"{RestVenue.Short(message)} on {what}.");
        }

        if ((int)answer.Status >= 400 || answer.Body is not JsonElement ok || !ok.TryGetProperty("result", out JsonElement result))
        {
            throw new VenueException(VenueFailure.Unavailable, $"Kraken answered {what} with HTTP {(int)answer.Status}.");
        }

        return result;
    }
}

/// <summary>Kraken's answers in the shape every venue reports. Pure, so it is tested on written-out answers.</summary>
public static class KrakenMapper
{
    private static readonly HashSet<string> LegacyCodes = ["XXBT", "XETH", "XLTC", "XXRP", "XXLM", "XXMR", "XZEC", "XETC", "XREP", "XMLN", "XXDG", "ZUSD", "ZEUR", "ZGBP", "ZCAD", "ZJPY", "ZAUD", "ZCHF"];

    public static VenueSnapshot Map(DateTimeOffset asOf, JsonElement balances, JsonElement tickers)
    {
        Dictionary<string, decimal> holdings = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty balance in balances.EnumerateObject())
        {
            // Fee credits buy nothing but lower fees and have no price; they are not part of the account's value.
            if (balance.Name.Equals("KFEE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            RestVenue.Add(holdings, Asset(balance.Name), decimal.Parse(balance.Value.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture));
        }

        return new VenueSnapshot(asOf, RestVenue.Holdings("Kraken", holdings, asset => Price(asset, tickers)), null, ["Kraken Futures is a separate system with its own keys and is not read."]);
    }

    public static IReadOnlyList<VenueTransfer> Transfers(JsonElement page)
    {
        List<VenueTransfer> result = [];
        if (!page.TryGetProperty("ledger", out JsonElement ledger) || ledger.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (JsonProperty entry in ledger.EnumerateObject())
        {
            string type = RestVenue.Text(entry.Value, "type");
            // Kraken Futures is not read, so money sent there has left the book and money coming back has arrived.
            // Other transfers (staking, airdrops) stay inside what is read.
            bool futures = type == "transfer" && RestVenue.Text(entry.Value, "subtype") is "spottofutures" or "spotfromfutures";
            if (type is not ("deposit" or "withdrawal") && !futures)
            {
                continue;
            }

            // A withdrawal's amount is already negative; the fee comes off either way.
            decimal quantity = (RestVenue.Number(entry.Value, "amount") ?? 0m) - (RestVenue.Number(entry.Value, "fee") ?? 0m);
            DateTimeOffset at = DateTimeOffset.FromUnixTimeMilliseconds((long)((RestVenue.Number(entry.Value, "time") ?? 0m) * 1000m));
            if (quantity != 0m)
            {
                result.Add(new VenueTransfer(entry.Name, at, Asset(RestVenue.Text(entry.Value, "asset")), quantity));
            }
        }

        return result;
    }

    /// <summary>
    /// Kraken's own names: XXBT and XBT are BTC, XETH is ETH, ZUSD is USD, XXDG is DOGE; a suffix after a dot
    /// (ETH2.S, USD.M, DOT.F) marks the same coin staked or lent.
    /// </summary>
    public static string Asset(string code)
    {
        string asset = code.Split('.')[0].ToUpperInvariant();
        // Only Kraken's oldest listings carry the extra first letter. Stripping it from any four-letter name would turn ZETA into ETA.
        if (LegacyCodes.Contains(asset))
        {
            asset = asset[1..];
        }

        return asset switch
        {
            "XBT" => "BTC",
            "XDG" => "DOGE",
            "ETH2" => "ETH",
            _ => asset,
        };
    }

    private static decimal? Price(string asset, JsonElement tickers)
    {
        string kraken = asset switch
        {
            "BTC" => "XBT",
            "DOGE" => "XDG",
            _ => asset,
        };
        foreach (string pair in new[] { $"X{kraken}ZUSD", $"Z{kraken}ZUSD", $"{kraken}USD", $"{kraken}ZUSD" })
        {
            // "c" is the last trade: price, then size.
            if (tickers.TryGetProperty(pair, out JsonElement ticker) && ticker.TryGetProperty("c", out JsonElement last) && last.ValueKind == JsonValueKind.Array && last.GetArrayLength() > 0)
            {
                return decimal.Parse(last[0].GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }

        // Kraken quotes some currencies the other way round: yen, Canadian dollars and Swiss francs per US dollar.
        foreach (string pair in new[] { $"ZUSDZ{kraken}", $"USD{kraken}" })
        {
            if (tickers.TryGetProperty(pair, out JsonElement ticker) && ticker.TryGetProperty("c", out JsonElement last) && last.ValueKind == JsonValueKind.Array && last.GetArrayLength() > 0)
            {
                decimal perDollar = decimal.Parse(last[0].GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture);
                return perDollar > 0m ? 1m / perDollar : null;
            }
        }

        return null;
    }
}
