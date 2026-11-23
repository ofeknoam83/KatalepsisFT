using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Domain.Instruments;
using KatalepsisFT.Infrastructure.Health;

namespace KatalepsisFT.Venues.Hyperliquid;

/// <summary>
/// Hyperliquid by wallet address, from its public "info" endpoint: the perp account, spot balances with their prices,
/// and the ledger of deposits, withdrawals and transfers. Nothing is signed, so nothing can be traded. Written new;
/// Katalepsis has no Hyperliquid client. Not read: money placed in vaults, and staked HYPE.
/// </summary>
public sealed partial class HyperliquidAdapter(HttpClient http, TimeProvider clock) : IVenueAdapter
{
    public const string HttpClientName = "venue:hyperliquid";
    public const string InfoUrl = "https://api.hyperliquid.xyz/info";

    public Venue Venue => Venue.Hyperliquid;

    public string DisplayName => "Hyperliquid";

    public bool NeedsPassphrase => false;

    public bool UsesAddress => true;

    public string WorkerGroup => WorkerGroups.Defi;

    /// <summary>Hyperliquid weighs requests per caller address; a read with spot holdings is among the heavier ones.</summary>
    public TimeSpan MinReadInterval => TimeSpan.FromSeconds(5);

    public async Task<CredentialCheck> CheckCredentialsAsync(VenueCredentials credentials, CancellationToken cancellationToken)
    {
        // Any well-formed address answers, with an empty account if it was never used; that is not an error.
        await InfoAsync(new { type = "clearinghouseState", user = Address(credentials) }, cancellationToken);
        return new CredentialCheck(false, false);
    }

    public async Task<VenueSnapshot> GetSnapshotAsync(VenueCredentials credentials, CancellationToken cancellationToken)
    {
        string address = Address(credentials);
        JsonElement perps = await InfoAsync(new { type = "clearinghouseState", user = address }, cancellationToken);
        JsonElement spot = await InfoAsync(new { type = "spotClearinghouseState", user = address }, cancellationToken);
        JsonElement? spotMeta = null,
            mids = null;
        if (HyperliquidMapper.HoldsSpotCoins(spot))
        {
            spotMeta = await InfoAsync(new { type = "spotMeta" }, cancellationToken);
            mids = await InfoAsync(new { type = "allMids" }, cancellationToken);
        }

        VenueSnapshot snapshot = HyperliquidMapper.Map(clock.GetUtcNow(), perps, spot, spotMeta, mids);
        // Money placed in vaults is still the account's, at what the vault says the share is worth now. Without it a
        // deposit into a vault would look like money lost.
        JsonElement vaults = await InfoAsync(new { type = "userVaultEquities", user = address }, cancellationToken);
        decimal inVaults = HyperliquidMapper.VaultEquity(vaults);
        return inVaults == 0m
            ? snapshot
            : snapshot with
            {
                Positions = [.. snapshot.Positions, new VenuePosition(InstrumentType.Cash, "USDC", inVaults, 1m)],
                ReportedEquityUsd = snapshot.ReportedEquityUsd + Math.Round(inVaults, 2),
                Notes = [.. snapshot.Notes ?? [], $"Includes {inVaults:N2} USD held in vaults, shown as USDC."],
            };
    }

    public async Task<IReadOnlyList<VenueTransfer>> GetTransfersAsync(VenueCredentials credentials, DateTimeOffset since, CancellationToken cancellationToken)
    {
        string address = Address(credentials);
        JsonElement ledger = await InfoAsync(new { type = "userNonFundingLedgerUpdates", user = address, startTime = since.ToUnixTimeMilliseconds() }, cancellationToken);
        return HyperliquidMapper.Transfers(ledger, address);
    }

    private static string Address(VenueCredentials credentials)
    {
        string address = credentials.ApiKey.Trim().ToLowerInvariant();
        return AddressPattern().IsMatch(address) ? address : throw new VenueException(VenueFailure.Rejected, "a Hyperliquid address is 0x followed by 40 letters a–f and digits.");
    }

    private async Task<JsonElement> InfoAsync(object request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        string body;
        try
        {
            response = await http.PostAsJsonAsync(InfoUrl, request, cancellationToken);
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new VenueException(VenueFailure.Unavailable, "Hyperliquid did not answer.", e);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // 422 is how it answers a request it cannot read, such as a malformed address.
                throw new VenueException((int)response.StatusCode == 422 ? VenueFailure.Rejected : VenueFailure.Unavailable, $"Hyperliquid answered with HTTP {(int)response.StatusCode}.");
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(body);
                return document.RootElement.Clone();
            }
            catch (JsonException e)
            {
                throw new VenueException(VenueFailure.Unavailable, "Hyperliquid answered with something unreadable.", e);
            }
        }
    }

    [GeneratedRegex("^0x[0-9a-f]{40}$")]
    private static partial Regex AddressPattern();
}

/// <summary>Hyperliquid's answers in the shape every venue reports. Pure, so it is tested on recorded answers.</summary>
public static class HyperliquidMapper
{
    public static bool HoldsSpotCoins(JsonElement spot) => spot.GetProperty("balances").EnumerateArray().Any(b => !VenueText.IsCash(Text(b, "coin")) && Number(b, "total") != 0m);

    public static VenueSnapshot Map(DateTimeOffset asOf, JsonElement perps, JsonElement spot, JsonElement? spotMeta, JsonElement? mids)
    {
        List<VenuePosition> result = [];
        decimal accountValue = Number(perps.GetProperty("marginSummary"), "accountValue");
        decimal openProfit = 0m;
        foreach (JsonElement entry in perps.GetProperty("assetPositions").EnumerateArray())
        {
            JsonElement position = entry.GetProperty("position");
            decimal size = Number(position, "szi");
            if (size == 0m)
            {
                continue;
            }

            string coin = Text(position, "coin");
            decimal profit = Number(position, "unrealizedPnl");
            openProfit += profit;
            // The size is signed and in coins; the mark price is the position's value over its size.
            decimal mark = Math.Abs(Number(position, "positionValue") / size);
            decimal entryPrice = Number(position, "entryPx");
            result.Add(new VenuePosition(InstrumentType.Perp, PerpAsset(coin), size, mark, entryPrice == 0m ? null : entryPrice, profit));
        }

        // The account's value counts open profit; taking it out leaves the USDC actually held as collateral.
        Dictionary<string, decimal> cash = new(StringComparer.OrdinalIgnoreCase);
        decimal collateral = accountValue - openProfit;
        if (collateral != 0m)
        {
            cash["USDC"] = collateral;
        }

        decimal spotValue = 0m;
        foreach (JsonElement balance in spot.GetProperty("balances").EnumerateArray())
        {
            string coin = Text(balance, "coin");
            decimal total = Number(balance, "total");
            if (total == 0m)
            {
                continue;
            }

            if (VenueText.IsCash(coin))
            {
                cash[coin] = cash.GetValueOrDefault(coin) + total;
                spotValue += total;
                continue;
            }

            decimal price = SpotPrice(coin, spotMeta, mids);
            spotValue += total * price;
            result.Add(new VenuePosition(InstrumentType.Spot, SpotAsset(coin), total, price));
        }

        result.InsertRange(0, cash.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => new VenuePosition(InstrumentType.Cash, c.Key.ToUpperInvariant(), c.Value, 1m)));
        return new VenueSnapshot(asOf, result, Math.Round(accountValue + spotValue, 2));
    }

    /// <summary>What the address holds in vaults, in USD.</summary>
    public static decimal VaultEquity(JsonElement vaults) => vaults.ValueKind == JsonValueKind.Array ? vaults.EnumerateArray().Sum(v => Number(v, "equity")) : 0m;

    /// <summary>Deposits, withdrawals and transfers to or from other addresses. Moves between the user's own perp and spot accounts are not flows.</summary>
    public static IReadOnlyList<VenueTransfer> Transfers(JsonElement ledger, string address)
    {
        List<VenueTransfer> result = [];
        foreach (JsonElement entry in ledger.EnumerateArray())
        {
            JsonElement delta = entry.GetProperty("delta");
            string id = Text(entry, "hash") + ":" + Text(delta, "type");
            DateTimeOffset at = DateTimeOffset.FromUnixTimeMilliseconds(entry.GetProperty("time").GetInt64());
            bool outgoing = !Text(delta, "destination").Equals(address, StringComparison.OrdinalIgnoreCase);
            switch (Text(delta, "type"))
            {
                case "deposit":
                    result.Add(new VenueTransfer(id, at, "USDC", Number(delta, "usdc")));
                    break;
                case "withdraw":
                    // The fee leaves the account with the withdrawal.
                    result.Add(new VenueTransfer(id, at, "USDC", -(Number(delta, "usdc") + Number(delta, "fee"))));
                    break;
                case "internalTransfer":
                    result.Add(new VenueTransfer(id, at, "USDC", outgoing ? -(Number(delta, "usdc") + Number(delta, "fee")) : Number(delta, "usdc")));
                    break;
                case "send" when Text(delta, "user").Equals(Text(delta, "destination"), StringComparison.OrdinalIgnoreCase):
                    // Between the address's own accounts.
                    break;
                case "send":
                case "spotTransfer":
                    result.Add(new VenueTransfer(id, at, SpotAsset(Text(delta, "token")), outgoing ? -Number(delta, "amount") : Number(delta, "amount")));
                    break;
                default:
                    // Perp-to-spot moves, vault deposits, liquidations, rewards: inside the account, or not transfers at all.
                    break;
            }
        }

        return result.Where(t => t.Quantity != 0m).ToList();
    }

    /// <summary>The price of a spot coin in USDC: its pair against USDC in the spot listing, at that pair's mid price.</summary>
    private static decimal SpotPrice(string coin, JsonElement? spotMeta, JsonElement? mids)
    {
        if (spotMeta is JsonElement meta && mids is JsonElement prices)
        {
            int? token = meta.GetProperty("tokens").EnumerateArray().Where(t => Text(t, "name") == coin).Select(t => (int?)t.GetProperty("index").GetInt32()).FirstOrDefault();
            foreach (JsonElement pair in meta.GetProperty("universe").EnumerateArray())
            {
                JsonElement tokens = pair.GetProperty("tokens");
                // Token 0 is USDC.
                if (token is not null && tokens[0].GetInt32() == token && tokens[1].GetInt32() == 0 && prices.TryGetProperty(Text(pair, "name"), out JsonElement mid))
                {
                    decimal price = decimal.Parse(mid.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture);
                    if (price > 0m)
                    {
                        return price;
                    }
                }
            }
        }

        throw new VenueException(VenueFailure.Unavailable, $"Hyperliquid holds {coin} but lists no USDC price for it.");
    }

    /// <summary>"kPEPE" is a thousand PEPE per unit; the book keeps it under its own name rather than guess at conversions.</summary>
    private static string PerpAsset(string coin) => coin.ToUpperInvariant();

    /// <summary>Bridged coins carry a U in front: UBTC is BTC, UETH is ETH, USOL is SOL.</summary>
    private static string SpotAsset(string coin) => coin.ToUpperInvariant() switch
    {
        "UBTC" => "BTC",
        "UETH" => "ETH",
        "USOL" => "SOL",
        string other => other,
    };

    private static string Text(JsonElement parent, string property) => parent.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    /// <summary>Hyperliquid writes numbers as strings; a missing one counts as zero.</summary>
    private static decimal Number(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal parsed) ? parsed : 0m;
}
