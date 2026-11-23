using System.Globalization;
using System.Net.Http.Json;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Domain.Instruments;
using KatalepsisFT.Infrastructure.Health;

namespace KatalepsisFT.Venues.Chains;

/// <summary>
/// What every on-chain wallet has in common: it is read by address from public nodes, nothing can be moved through
/// it, balances are valued at Binance's public prices, and the chain's own history of transfers is not read, because
/// that needs an indexing provider this system does not have yet.
/// </summary>
public abstract partial class WalletAdapter(HttpClient http, TimeProvider clock) : IVenueAdapter
{
    public const string HttpClientName = "venue:chains";
    public const string NoTransferHistory = "transfers in and out of an on-chain wallet are not read yet, so they will look like profit or loss. Reading them needs an indexing provider.";

    protected HttpClient Http => http;

    public abstract Venue Venue { get; }

    public abstract string DisplayName { get; }

    public bool NeedsPassphrase => false;

    public bool UsesAddress => true;

    public string WorkerGroup => WorkerGroups.Defi;

    /// <summary>The address in the form the chain's nodes expect, or a <see cref="VenueException"/> saying what an address looks like.</summary>
    protected abstract string Address(VenueCredentials credentials);

    /// <summary>What the address holds, per asset, with any parts that could not be read put into words.</summary>
    protected abstract Task<(Dictionary<string, decimal> Holdings, List<string> Notes)> ReadAsync(string address, CancellationToken cancellationToken);

    public async Task<CredentialCheck> CheckCredentialsAsync(VenueCredentials credentials, CancellationToken cancellationToken)
    {
        // Reading it once proves the address is one the chain knows how to answer for.
        await ReadAsync(Address(credentials), cancellationToken);
        return new CredentialCheck(false, false);
    }

    public async Task<VenueSnapshot> GetSnapshotAsync(VenueCredentials credentials, CancellationToken cancellationToken)
    {
        (Dictionary<string, decimal> holdings, List<string> notes) = await ReadAsync(Address(credentials), cancellationToken);
        IReadOnlyDictionary<string, decimal> prices = holdings.Keys.Any(a => !VenueText.IsCash(a)) ? WalletMath.Prices(await GetJsonAsync(ChainData.PricesUrl, "Binance prices", cancellationToken)) : new Dictionary<string, decimal>();
        return WalletMath.Snapshot(clock.GetUtcNow(), holdings, prices, notes);
    }

    public Task<IReadOnlyList<VenueTransfer>> GetTransfersAsync(VenueCredentials credentials, DateTimeOffset since, CancellationToken cancellationToken) =>
        throw new VenueException(VenueFailure.Unavailable, NoTransferHistory);

    protected async Task<JsonElement> GetJsonAsync(string url, string what, CancellationToken cancellationToken, bool notFoundIsRejection = false)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        return await SendAsync(request, what, notFoundIsRejection, cancellationToken);
    }

    /// <summary>One JSON-RPC call, or several in one request when <paramref name="body"/> is a list.</summary>
    protected async Task<JsonElement> PostJsonAsync(string url, object body, string what, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        return await SendAsync(request, what, false, cancellationToken);
    }

    private async Task<JsonElement> SendAsync(HttpRequestMessage request, string what, bool notFoundIsRejection, CancellationToken cancellationToken)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", "KatalepsisFT");
        HttpResponseMessage response;
        string text;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
            text = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new VenueException(VenueFailure.Unavailable, $"{what} did not answer.", e);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                bool rejected = notFoundIsRejection && (int)response.StatusCode is 400 or 404;
                throw new VenueException(rejected ? VenueFailure.Rejected : VenueFailure.Unavailable, rejected ? "the chain does not recognise this address." : $"{what} answered with HTTP {(int)response.StatusCode}.");
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(text);
                return document.RootElement.Clone();
            }
            catch (JsonException e)
            {
                throw new VenueException(VenueFailure.Unavailable, $"{what} answered with something unreadable.", e);
            }
        }
    }

    [GeneratedRegex("^0x[0-9a-fA-F]{40}$")]
    protected static partial Regex EvmAddress();

    [GeneratedRegex("^[1-9A-HJ-NP-Za-km-z]{32,44}$")]
    protected static partial Regex SolanaAddress();

    [GeneratedRegex("^(bc1[0-9a-z]{25,87}|[13][1-9A-HJ-NP-Za-km-z]{25,34})$")]
    protected static partial Regex BitcoinAddress();

    [GeneratedRegex("^T[1-9A-HJ-NP-Za-km-z]{33}$")]
    protected static partial Regex TronAddress();
}

/// <summary>
/// One address on Ethereum, BNB Chain, Arbitrum, Base, Optimism, Polygon and Avalanche at once: the same address is
/// the same wallet on all of them. Reads the native coin and the tokens listed in <see cref="ChainData"/>.
/// </summary>
public sealed class EvmWalletAdapter(HttpClient http, TimeProvider clock) : WalletAdapter(http, clock)
{
    public override Venue Venue => Venue.EvmWallet;

    public override string DisplayName => "Wallet · Ethereum, BNB Chain, Arbitrum, Base, Optimism, Polygon, Avalanche";

    protected override string Address(VenueCredentials credentials)
    {
        string address = credentials.ApiKey.Trim();
        return EvmAddress().IsMatch(address) ? address.ToLowerInvariant() : throw new VenueException(VenueFailure.Rejected, "an address on these chains is 0x followed by 40 letters a–f and digits.");
    }

    protected override async Task<(Dictionary<string, decimal> Holdings, List<string> Notes)> ReadAsync(string address, CancellationToken cancellationToken)
    {
        Dictionary<string, decimal> holdings = new(StringComparer.OrdinalIgnoreCase);
        List<string> notes = [];
        // The chains are independent of each other, so they are asked at the same time.
        (EvmChain Chain, JsonElement? Answer, string? Error)[] answers = await Task.WhenAll(
            ChainData.EvmChains.Select(async chain =>
            {
                try
                {
                    return (chain, (JsonElement?)await PostJsonAsync(chain.RpcUrl, WalletMath.EvmRequests(chain, address), chain.Name, cancellationToken), (string?)null);
                }
                catch (VenueException e)
                {
                    return (chain, (JsonElement?)null, e.Message);
                }
            })
        );

        foreach ((EvmChain chain, JsonElement? answer, string? error) in answers)
        {
            try
            {
                if (answer is null)
                {
                    throw new VenueException(VenueFailure.Unavailable, error!);
                }

                foreach ((string asset, decimal quantity) in WalletMath.EvmBalances(chain, answer.Value))
                {
                    holdings[asset] = holdings.GetValueOrDefault(asset) + quantity;
                }
            }
            catch (VenueException e)
            {
                // One chain's node being down should not hide the wallet on the other six, but it must be said.
                notes.Add($"{chain.Name} was not read: {e.Message}");
                continue;
            }

            try
            {
                await ReadPoolsAsync(chain, address, holdings, notes, cancellationToken);
            }
            catch (VenueException e)
            {
                // The wallet's own balances above stand; what is missing is whatever sits in pools on this chain.
                notes.Add($"Pool positions on {chain.Name} were not read: {e.Message}");
            }
        }

        if (notes.Count == ChainData.EvmChains.Count)
        {
            throw new VenueException(VenueFailure.Unavailable, "none of the chains answered.");
        }

        return (holdings, notes);
    }

    /// <summary>
    /// Adds what the address holds in Uniswap v3 and Aerodrome concentrated pools on one chain, as the tokens inside
    /// each position at the pool's current price.
    /// </summary>
    private async Task ReadPoolsAsync(EvmChain chain, string address, Dictionary<string, decimal> holdings, List<string> notes, CancellationToken cancellationToken)
    {
        List<PositionManager> managers = ChainData.PositionManagers.Where(m => m.Chain == chain.Name).ToList();
        if (managers.Count == 0)
        {
            return;
        }

        async Task<string[]> CallAsync(List<(string To, string Data)> calls, string what) =>
            calls.Count == 0 ? [] : PoolPositions.Results(await PostJsonAsync(chain.RpcUrl, calls.Select((c, i) => PoolPositions.EthCall(i, c.To, c.Data)).ToList(), chain.Name, cancellationToken), calls.Count, what);

        string[] counts = await CallAsync(managers.Select(m => (m.Contract, PoolPositions.CountCall(address))).ToList(), "position counts");
        List<(PositionManager Manager, int Index)> slots = [];
        for (int m = 0; m < managers.Count; m++)
        {
            int count = (int)System.Numerics.BigInteger.Min(PoolPositions.Number(counts[m]), 100_000);
            if (count > PoolPositions.MaxPerManager)
            {
                notes.Add($"This address has {count} {managers[m].Protocol} positions on {chain.Name}; only the first {PoolPositions.MaxPerManager} are read.");
            }

            slots.AddRange(Enumerable.Range(0, Math.Min(count, PoolPositions.MaxPerManager)).Select(i => (managers[m], i)));
        }

        if (slots.Count == 0)
        {
            return;
        }

        string[] ids = await CallAsync(slots.Select(s => (s.Manager.Contract, PoolPositions.TokenIdCall(address, s.Index))).ToList(), "position numbers");
        string[] raw = await CallAsync(slots.Select((s, i) => (s.Manager.Contract, PoolPositions.PositionCall(PoolPositions.Number(ids[i])))).ToList(), "positions");
        string[] factories = await CallAsync(managers.Select(m => (m.Contract, PoolPositions.FactoryCall)).ToList(), "factories");

        Dictionary<string, ChainToken> known = chain.Tokens.ToDictionary(t => t.Contract.ToLowerInvariant());
        List<(PositionManager Manager, PoolPosition Position)> open = [];
        Dictionary<string, int> uncounted = [];
        for (int i = 0; i < slots.Count; i++)
        {
            PoolPosition position = PoolPositions.ParsePosition(raw[i]);
            if (position.Liquidity.IsZero && position.Owed0.IsZero && position.Owed1.IsZero)
            {
                continue;
            }

            // A token outside the checked list has no trusted price, and anyone can name a worthless token "USDC".
            if (known.ContainsKey(position.Token0.ToLowerInvariant()) && known.ContainsKey(position.Token1.ToLowerInvariant()))
            {
                open.Add((slots[i].Manager, position));
            }
            else
            {
                uncounted[slots[i].Manager.Protocol] = uncounted.GetValueOrDefault(slots[i].Manager.Protocol) + 1;
            }
        }

        foreach ((string protocol, int count) in uncounted)
        {
            notes.Add($"{count} {protocol} position(s) on {chain.Name} hold a token outside the list of main tokens and are not counted.");
        }

        if (open.Count == 0)
        {
            return;
        }

        string[] pools = await CallAsync(open.Select(o => (PoolPositions.AddressOf(factories[managers.IndexOf(o.Manager)]), PoolPositions.PoolCall(o.Manager, o.Position))).ToList(), "pools");
        string[] prices = await CallAsync(pools.Select(p => (PoolPositions.AddressOf(p), PoolPositions.PriceCall)).ToList(), "pool prices");
        for (int i = 0; i < open.Count; i++)
        {
            PoolPosition position = open[i].Position;
            (double amount0, double amount1) = PoolPositions.Amounts(position.Liquidity, PoolPositions.ParseSqrtPrice(prices[i]), position.TickLower, position.TickUpper);
            foreach ((string token, double amount, System.Numerics.BigInteger owed) in new[] { (position.Token0, amount0, position.Owed0), (position.Token1, amount1, position.Owed1) })
            {
                ChainToken info = known[token.ToLowerInvariant()];
                decimal quantity = (decimal)((amount + (double)owed) / Math.Pow(10, info.Decimals));
                if (quantity != 0m)
                {
                    holdings[info.Asset] = holdings.GetValueOrDefault(info.Asset) + Math.Round(quantity, 8);
                }
            }
        }

        foreach (IGrouping<string, (PositionManager Manager, PoolPosition Position)> group in open.GroupBy(o => o.Manager.Protocol))
        {
            notes.Add($"{group.Count()} {group.Key} position(s) on {chain.Name} are counted as the tokens inside them; fees earned but not collected are not included.");
        }
    }
}

/// <summary>A Solana address: SOL and the tokens listed in <see cref="ChainData"/>.</summary>
public sealed class SolanaWalletAdapter(HttpClient http, TimeProvider clock) : WalletAdapter(http, clock)
{
    public override Venue Venue => Venue.SolanaWallet;

    /// <summary>Between two requests, to stay inside the public endpoint's limit.</summary>
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(400);

    public override string DisplayName => "Wallet · Solana";

    protected override string Address(VenueCredentials credentials)
    {
        string address = credentials.ApiKey.Trim();
        return SolanaAddress().IsMatch(address) ? address : throw new VenueException(VenueFailure.Rejected, "a Solana address is 32 to 44 letters and digits.");
    }

    protected override async Task<(Dictionary<string, decimal> Holdings, List<string> Notes)> ReadAsync(string address, CancellationToken cancellationToken)
    {
        // One question per token, not "every token of this address": some addresses hold so many that the answer does not fit.
        // And one request per question: the public endpoint turns away several of these sent together.
        List<JsonElement> answers = [await PostJsonAsync(ChainData.SolanaRpcUrl, new { jsonrpc = "2.0", id = 0, method = "getBalance", @params = new object[] { address } }, "Solana", cancellationToken)];
        for (int i = 0; i < ChainData.SolanaTokens.Count; i++)
        {
            await Task.Delay(Pause, cancellationToken);
            object request = new { jsonrpc = "2.0", id = i + 1, method = "getTokenAccountsByOwner", @params = new object[] { address, new { mint = ChainData.SolanaTokens[i].Contract }, new { encoding = "jsonParsed" } } };
            answers.Add(await PostJsonAsync(ChainData.SolanaRpcUrl, request, "Solana", cancellationToken));
        }

        JsonElement answer = JsonSerializer.SerializeToElement(answers);
        return (WalletMath.SolanaBalances(answer), []);
    }
}

/// <summary>A Bitcoin address: its confirmed balance.</summary>
public sealed class BitcoinWalletAdapter(HttpClient http, TimeProvider clock) : WalletAdapter(http, clock)
{
    public override Venue Venue => Venue.BitcoinWallet;

    public override string DisplayName => "Wallet · Bitcoin";

    protected override string Address(VenueCredentials credentials)
    {
        string address = credentials.ApiKey.Trim();
        return BitcoinAddress().IsMatch(address) ? address : throw new VenueException(VenueFailure.Rejected, "a Bitcoin address starts with bc1, 1 or 3.");
    }

    protected override async Task<(Dictionary<string, decimal> Holdings, List<string> Notes)> ReadAsync(string address, CancellationToken cancellationToken)
    {
        JsonElement answer = await GetJsonAsync($"{ChainData.BitcoinApiUrl}/address/{address}", "Bitcoin", cancellationToken, notFoundIsRejection: true);
        return (WalletMath.BitcoinBalance(answer), []);
    }
}

/// <summary>A Tron address: TRX and USDT.</summary>
public sealed class TronWalletAdapter(HttpClient http, TimeProvider clock) : WalletAdapter(http, clock)
{
    public override Venue Venue => Venue.TronWallet;

    public override string DisplayName => "Wallet · Tron";

    protected override string Address(VenueCredentials credentials)
    {
        string address = credentials.ApiKey.Trim();
        return TronAddress().IsMatch(address) ? address : throw new VenueException(VenueFailure.Rejected, "a Tron address is T followed by 33 letters and digits.");
    }

    protected override async Task<(Dictionary<string, decimal> Holdings, List<string> Notes)> ReadAsync(string address, CancellationToken cancellationToken)
    {
        JsonElement answer = await GetJsonAsync($"{ChainData.TronApiUrl}/v1/accounts/{address}", "Tron", cancellationToken, notFoundIsRejection: true);
        return (WalletMath.TronBalances(answer), []);
    }
}

/// <summary>Turning the chains' answers into holdings. Pure, so it is tested on recorded answers.</summary>
public static class WalletMath
{
    private const string BalanceOf = "0x70a08231";

    /// <summary>One request per chain: the native balance first, then each listed token's balance, in order.</summary>
    public static List<object> EvmRequests(EvmChain chain, string address)
    {
        string padded = address[2..].ToLowerInvariant().PadLeft(64, '0');
        List<object> requests = [new { jsonrpc = "2.0", id = 0, method = "eth_getBalance", @params = new object[] { address, "latest" } }];
        requests.AddRange(chain.Tokens.Select((token, index) => (object)new { jsonrpc = "2.0", id = index + 1, method = "eth_call", @params = new object[] { new { to = token.Contract, data = BalanceOf + padded }, "latest" } }));
        return requests;
    }

    public static Dictionary<string, decimal> EvmBalances(EvmChain chain, JsonElement answers)
    {
        Dictionary<int, JsonElement> byId = ById(answers, chain.Name);
        Dictionary<string, decimal> result = new(StringComparer.OrdinalIgnoreCase);
        for (int id = 0; id <= chain.Tokens.Count; id++)
        {
            (string asset, int decimals) = id == 0 ? (chain.Native, 18) : (chain.Tokens[id - 1].Asset, chain.Tokens[id - 1].Decimals);
            // Every question must be answered: a missing answer read as zero would drop a holding without a word.
            if (!byId.TryGetValue(id, out JsonElement answer) || !answer.TryGetProperty("result", out JsonElement value) || value.ValueKind != JsonValueKind.String)
            {
                throw new VenueException(VenueFailure.Unavailable, $"its node gave no balance for {asset}.");
            }

            decimal quantity = Units(value.GetString()!, decimals);
            if (quantity != 0m)
            {
                result[asset] = result.GetValueOrDefault(asset) + quantity;
            }
        }

        return result;
    }

    public static Dictionary<string, decimal> SolanaBalances(JsonElement answers)
    {
        Dictionary<int, JsonElement> byId = ById(answers, "Solana");
        Dictionary<string, decimal> result = new(StringComparer.OrdinalIgnoreCase);
        decimal lamports = Result(byId, 0, "SOL").GetProperty("value").GetDecimal();
        if (lamports != 0m)
        {
            result["SOL"] = lamports / 1_000_000_000m;
        }

        for (int i = 0; i < ChainData.SolanaTokens.Count; i++)
        {
            ChainToken token = ChainData.SolanaTokens[i];
            // An address can have several accounts for one token; they add up.
            decimal quantity = Result(byId, i + 1, token.Asset)
                .GetProperty("value")
                .EnumerateArray()
                .Sum(account => decimal.Parse(account.GetProperty("account").GetProperty("data").GetProperty("parsed").GetProperty("info").GetProperty("tokenAmount").GetProperty("uiAmountString").GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture));
            if (quantity != 0m)
            {
                result[token.Asset] = result.GetValueOrDefault(token.Asset) + quantity;
            }
        }

        return result;
    }

    /// <summary>Confirmed coins received less confirmed coins spent, in satoshis.</summary>
    public static Dictionary<string, decimal> BitcoinBalance(JsonElement answer)
    {
        JsonElement stats = answer.GetProperty("chain_stats");
        decimal satoshis = stats.GetProperty("funded_txo_sum").GetDecimal() - stats.GetProperty("spent_txo_sum").GetDecimal();
        return satoshis == 0m ? [] : new Dictionary<string, decimal> { ["BTC"] = satoshis / 100_000_000m };
    }

    public static Dictionary<string, decimal> TronBalances(JsonElement answer)
    {
        Dictionary<string, decimal> result = new(StringComparer.OrdinalIgnoreCase);
        // An address that has never been used answers with an empty list; that is an empty wallet, not an error.
        JsonElement data = answer.GetProperty("data");
        if (data.GetArrayLength() == 0)
        {
            return result;
        }

        JsonElement account = data[0];
        if (account.TryGetProperty("balance", out JsonElement sun) && sun.GetDecimal() != 0m)
        {
            result["TRX"] = sun.GetDecimal() / 1_000_000m;
        }

        if (account.TryGetProperty("trc20", out JsonElement tokens))
        {
            foreach (JsonElement token in tokens.EnumerateArray())
            {
                if (token.TryGetProperty(ChainData.TronUsdt.Contract, out JsonElement amount))
                {
                    decimal quantity = decimal.Parse(amount.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture) / 1_000_000m;
                    if (quantity != 0m)
                    {
                        result["USDT"] = result.GetValueOrDefault("USDT") + quantity;
                    }
                }
            }
        }

        return result;
    }

    /// <summary>Binance's list of prices as asset → USD, from each asset's USDT pair.</summary>
    public static IReadOnlyDictionary<string, decimal> Prices(JsonElement tickers)
    {
        Dictionary<string, decimal> prices = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement ticker in tickers.EnumerateArray())
        {
            string symbol = ticker.GetProperty("symbol").GetString()!;
            if (symbol.EndsWith("USDT", StringComparison.Ordinal) && decimal.TryParse(ticker.GetProperty("price").GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal price) && price > 0m)
            {
                prices[symbol[..^4]] = price;
            }
        }

        return prices;
    }

    public static VenueSnapshot Snapshot(DateTimeOffset asOf, IReadOnlyDictionary<string, decimal> holdings, IReadOnlyDictionary<string, decimal> prices, IReadOnlyList<string> notes)
    {
        List<VenuePosition> positions = [];
        List<string> all = [.. notes];
        foreach ((string asset, decimal quantity) in holdings.OrderBy(h => h.Key, StringComparer.Ordinal))
        {
            string symbol = asset.ToUpperInvariant();
            if (VenueText.IsCash(symbol))
            {
                positions.Add(new VenuePosition(InstrumentType.Cash, symbol, quantity, 1m));
            }
            else if (prices.TryGetValue(symbol, out decimal price))
            {
                positions.Add(new VenuePosition(InstrumentType.Spot, symbol, quantity, price));
            }
            else
            {
                all.Add($"{quantity.ToString("0.########", CultureInfo.InvariantCulture)} {symbol} is held but has no price on Binance, so it is not counted.");
            }
        }

        all.Add("Only the main tokens of each chain are read; other tokens, staked coins and positions in protocols are not shown.");
        return new VenueSnapshot(asOf, positions, null, all);
    }

    /// <summary>A hex number of the smallest units, as whole coins.</summary>
    public static decimal Units(string hex, int decimals)
    {
        string digits = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex;
        if (digits.Length == 0)
        {
            throw new VenueException(VenueFailure.Unavailable, "a node answered a balance with nothing.");
        }

        // The leading zero keeps the number positive whatever its first digit.
        BigInteger raw = BigInteger.Parse("0" + digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        BigInteger whole = BigInteger.DivRem(raw, BigInteger.Pow(10, decimals), out BigInteger remainder);
        return (decimal)whole + (decimal)remainder / (decimal)BigInteger.Pow(10, decimals);
    }

    private static Dictionary<int, JsonElement> ById(JsonElement answers, string chain)
    {
        if (answers.ValueKind != JsonValueKind.Array)
        {
            string reason = answers.ValueKind == JsonValueKind.Object && answers.TryGetProperty("error", out JsonElement error) && error.TryGetProperty("message", out JsonElement message) ? message.GetString() ?? "an error" : "an unexpected answer";
            throw new VenueException(VenueFailure.Unavailable, $"{chain}: {reason}.");
        }

        return answers.EnumerateArray().Where(a => a.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.Number).ToDictionary(a => a.GetProperty("id").GetInt32());
    }

    private static JsonElement Result(Dictionary<int, JsonElement> byId, int id, string asset) =>
        byId.TryGetValue(id, out JsonElement answer) && answer.TryGetProperty("result", out JsonElement result)
            ? result
            : throw new VenueException(VenueFailure.Unavailable, $"Solana gave no balance for {asset}{(byId.TryGetValue(id, out JsonElement failed) && failed.TryGetProperty("error", out JsonElement error) && error.TryGetProperty("message", out JsonElement message) ? ": " + message.GetString() : string.Empty)}.");
}
