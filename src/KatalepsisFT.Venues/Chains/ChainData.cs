namespace KatalepsisFT.Venues.Chains;

/// <param name="Asset">What the token counts as in the book: wrapped and bridged forms count as the coin itself.</param>
public sealed record ChainToken(string Contract, string Asset, int Decimals);

/// <param name="ByTickSpacing">Aerodrome's concentrated pools are found by tick spacing where Uniswap's are found by fee.</param>
public sealed record PositionManager(string Chain, string Protocol, string Contract, bool ByTickSpacing);

/// <param name="Native">The coin fees are paid in.</param>
public sealed record EvmChain(string Name, string RpcUrl, string Native, IReadOnlyList<ChainToken> Tokens);

/// <summary>
/// The chains and tokens a wallet is read on. Every contract below was checked on its chain on 23 November 2026 by
/// asking the contract itself for its symbol and decimals; an address that did not answer was left out. Tokens not
/// listed here are not read: finding every token of an address needs an indexing provider.
/// </summary>
public static class ChainData
{
    public static readonly IReadOnlyList<EvmChain> EvmChains =
    [
        new("Ethereum", "https://ethereum-rpc.publicnode.com", "ETH",
        [
            new("0xdAC17F958D2ee523a2206206994597C13D831ec7", "USDT", 6),
            new("0xA0b86991c6218b36c1d19D4a2e9Eb0cE3606eB48", "USDC", 6),
            new("0x6B175474E89094C44Da98b954EedeAC495271d0F", "DAI", 18),
            new("0xC02aaA39b223FE8D0A0e5C4F27eAD9083C756Cc2", "ETH", 18), // WETH
            new("0x2260FAC5E5542a773Aa44fBCfeDf7C193bc2C599", "BTC", 8), // WBTC
            new("0x514910771AF9Ca656af840dff83E8264EcF986CA", "LINK", 18),
            new("0x1f9840a85d5aF5bf1D1762F925BDADdC4201F984", "UNI", 18),
            new("0x7Fc66500c84A76Ad7e9c93437bFc5Ac33E2DDaE9", "AAVE", 18),
            new("0xfAbA6f8e4a5E8Ab82F62fe7C39859FA577269BE3", "ONDO", 18),
            new("0x6982508145454Ce325dDbE47a25d4ec3d2311933", "PEPE", 18),
            new("0x95aD61b0a150d79219dCF64E1E6Cc01f0B64C4cE", "SHIB", 18),
        ]),
        new("BNB Chain", "https://bsc-rpc.publicnode.com", "BNB",
        [
            new("0x55d398326f99059fF775485246999027B3197955", "USDT", 18),
            new("0x8AC76a51cc950d9822D68b83fE1Ad97B32Cd580d", "USDC", 18),
            new("0xbb4CdB9CBd36B01bD1cBaEBF2De08d9173bc095c", "BNB", 18), // WBNB
            new("0x7130d2A12B9BCbFAe4f2634d864A1Ee1Ce3Ead9c", "BTC", 18), // BTCB
            new("0x2170Ed0880ac9A755fd29B2688956BD959F933F8", "ETH", 18),
        ]),
        new("Arbitrum", "https://arbitrum-one-rpc.publicnode.com", "ETH",
        [
            new("0xaf88d065e77c8cC2239327C5EDb3A432268e5831", "USDC", 6),
            new("0xFF970A61A04b1cA14834A43f5dE4533eBDDB5CC8", "USDC", 6), // USDC.e
            new("0xFd086bC7CD5C481DCC9C85ebE478A1C0b69FCbb9", "USDT", 6),
            new("0x82aF49447D8a07e3bd95BD0d56f35241523fBab1", "ETH", 18), // WETH
            new("0x2f2a2543B76A4166549F7aaB2e75Bef0aefC5B0f", "BTC", 8), // WBTC
            new("0x912CE59144191C1204E64559FE8253a0e49E6548", "ARB", 18),
        ]),
        new("Base", "https://base-rpc.publicnode.com", "ETH",
        [
            new("0x833589fCD6eDb6E08f4c7C32D4f71b54bdA02913", "USDC", 6),
            new("0x4200000000000000000000000000000000000006", "ETH", 18), // WETH
            new("0xcbB7C0000aB88B473b1f5aFd9ef808440eed33Bf", "BTC", 8), // cbBTC
            new("0x940181a94A35A4569E4529A3CDfB74e38FD98631", "AERO", 18),
        ]),
        new("Optimism", "https://optimism-rpc.publicnode.com", "ETH",
        [
            new("0x0b2C639c533813f4Aa9D7837CAf62653d097Ff85", "USDC", 6),
            new("0x7F5c764cBc14f9669B88837ca1490cCa17c31607", "USDC", 6), // USDC.e
            new("0x94b008aA00579c1307B0EF2c499aD98a8ce58e58", "USDT", 6),
            new("0x4200000000000000000000000000000000000006", "ETH", 18), // WETH
            new("0x4200000000000000000000000000000000000042", "OP", 18),
        ]),
        new("Polygon", "https://polygon-bor-rpc.publicnode.com", "POL",
        [
            new("0x3c499c542cEF5E3811e1192ce70d8cC03d5c3359", "USDC", 6),
            new("0x2791Bca1f2de4661ED88A30C99A7a9449Aa84174", "USDC", 6), // USDC.e
            new("0xc2132D05D31c914a87C6611C10748AEb04B58e8F", "USDT", 6),
            new("0x7ceB23fD6bC0adD59E62ac25578270cFf1b9f619", "ETH", 18), // WETH
            new("0x1BFD67037B42Cf73acF2047067bd4F2C47D9BfD6", "BTC", 8), // WBTC
        ]),
        new("Avalanche", "https://avalanche-c-chain-rpc.publicnode.com", "AVAX",
        [
            new("0xB97EF9Ef8734C71904D8002F8b6Bc66Dd9c48a6E", "USDC", 6),
            new("0x9702230A8Ea53601f5cD2dc00fDBc13d4dF4A8c7", "USDT", 6),
            new("0xB31f66AA3C1e785363F0875A1B74E27b85FD66c7", "AVAX", 18), // WAVAX
            new("0x49D5c2BdFfac6CE2BFdB6640F4F80f226bc10bAB", "ETH", 18), // WETH.e
        ]),
    ];

    /// <summary>
    /// Contracts that hold liquidity-pool positions as tokens owned by the wallet. Each was checked on its chain: it
    /// names its factory, and a position read from it has the expected twelve fields.
    /// </summary>
    public static readonly IReadOnlyList<PositionManager> PositionManagers =
    [
        new("Ethereum", "Uniswap v3", "0xC36442b4a4522E871399CD717aBDD847Ab11FE88", false),
        new("Arbitrum", "Uniswap v3", "0xC36442b4a4522E871399CD717aBDD847Ab11FE88", false),
        new("Optimism", "Uniswap v3", "0xC36442b4a4522E871399CD717aBDD847Ab11FE88", false),
        new("Polygon", "Uniswap v3", "0xC36442b4a4522E871399CD717aBDD847Ab11FE88", false),
        new("Base", "Uniswap v3", "0x03a520b32C04BF3bEEf7BEb72E919cf822Ed34f1", false),
        new("BNB Chain", "Uniswap v3", "0x7b8A01B39D58278b5DE7e48c8449c9f4F5170613", false),
        new("Base", "Aerodrome", "0x827922686190790b37229fd06084350E74485b72", true),
    ];

    public const string SolanaRpcUrl = "https://api.mainnet-beta.solana.com";

    /// <summary>Solana tokens by mint address, each checked on chain for its decimals.</summary>
    public static readonly IReadOnlyList<ChainToken> SolanaTokens =
    [
        new("EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v", "USDC", 6),
        new("Es9vMFrzaCERmJfrF4H2FYD4KCoNkY11McCe8BenwNYB", "USDT", 6),
        new("JUPyiwrYJFskUPiHa7hkeR8VUtAeFoSYbKedZNsDvCN", "JUP", 6),
        new("DezXAZ8z7PnrnRJjz3wXBoRgixCa6xjnB7YaB1pPB263", "BONK", 5),
        new("EKpQGSJtjMFqKZ9KQanSqYXRcF8fBopzLHYxdM65zcjm", "WIF", 6),
    ];

    public const string BitcoinApiUrl = "https://mempool.space/api";
    public const string TronApiUrl = "https://api.trongrid.io";

    /// <summary>USDT on Tron.</summary>
    public static readonly ChainToken TronUsdt = new("TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t", "USDT", 6);

    /// <summary>Live USD prices for everything above, from Binance's public market data.</summary>
    public const string PricesUrl = "https://data-api.binance.vision/api/v3/ticker/price";
}
