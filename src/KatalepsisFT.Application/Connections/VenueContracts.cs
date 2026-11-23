using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Domain.Instruments;

namespace KatalepsisFT.Application.Connections;

public sealed record VenueCredentials(string ApiKey, string ApiSecret, string? Passphrase = null);

/// <param name="CanTrade">Null when the venue does not say.</param>
/// <param name="CanWithdraw">Null when the venue does not say.</param>
public sealed record CredentialCheck(bool? CanTrade, bool? CanWithdraw)
{
    public bool Verified => CanTrade is not null && CanWithdraw is not null;

    public bool IsReadOnly => CanTrade != true && CanWithdraw != true;
}

/// <summary>
/// One balance or position as every adapter reports it. Quantity is signed (negative is short) and counted in units of
/// the asset, so a venue's contract sizes and inverse contracts are resolved inside its adapter.
/// </summary>
/// <param name="Asset">Upper-case ticker of the underlying asset: BTC for BTC spot, a BTC perp or a BTC option.</param>
/// <param name="MarkPrice">USD per unit. For an option, the premium in USD per unit of the underlying.</param>
/// <param name="UnsettledPnlUsd">
/// For perps and futures: the profit not yet paid into the cash balance. This, not quantity × (mark − entry), is what
/// the position adds to NAV, because venues settle at different moments. Null lets the valuer derive it from the entry price.
/// </param>
public sealed record VenuePosition(
    InstrumentType Type,
    string Asset,
    decimal Quantity,
    decimal MarkPrice,
    decimal? EntryPrice = null,
    decimal? UnsettledPnlUsd = null,
    DateOnly? Expiry = null,
    decimal? Strike = null,
    OptionRight? OptionRight = null,
    decimal? UnderlyingPrice = null,
    decimal? Delta = null
);

/// <summary>Every balance and position of one account at one moment. Complete, or not produced at all.</summary>
/// <param name="ReportedEquityUsd">The account value the venue itself states, to compare with the value computed from the positions.</param>
/// <param name="Notes">Parts of the account that could not be read, in words. The positions given are still complete for the parts that were.</param>
public sealed record VenueSnapshot(DateTimeOffset AsOf, IReadOnlyList<VenuePosition> Positions, decimal? ReportedEquityUsd = null, IReadOnlyList<string>? Notes = null);

/// <summary>Money or coins moved into (positive) or out of (negative) the account from outside it. Not a trade, not a fee.</summary>
/// <param name="Id">The venue's own identifier, stable across reads, so a transfer is never counted twice.</param>
public sealed record VenueTransfer(string Id, DateTimeOffset At, string Asset, decimal Quantity);

public enum VenueFailure
{
    /// <summary>The venue refused the key: wrong, expired, revoked, or not allowed from this address.</summary>
    Rejected,

    /// <summary>The venue did not answer, limited the rate, or answered with something unreadable.</summary>
    Unavailable,
}

/// <summary>A read that did not produce a complete answer. Its message is safe to show and to store.</summary>
public sealed class VenueException(VenueFailure failure, string message, Exception? inner = null) : Exception(message, inner)
{
    public VenueFailure Failure { get; } = failure;
}

/// <summary>
/// The whole of what the system can do on a venue. There is no method that places, changes or cancels anything, so no
/// code path can trade.
/// </summary>
public interface IVenueAdapter
{
    Venue Venue { get; }

    string DisplayName { get; }

    bool NeedsPassphrase { get; }

    /// <summary>Which worker group reads this venue.</summary>
    string WorkerGroup { get; }

    /// <summary>Proves the key works and reports what it may do. Throws <see cref="VenueException"/>.</summary>
    Task<CredentialCheck> CheckCredentialsAsync(VenueCredentials credentials, CancellationToken cancellationToken);

    /// <summary>Reads the whole account. Throws <see cref="VenueException"/> rather than return a partial book.</summary>
    Task<VenueSnapshot> GetSnapshotAsync(VenueCredentials credentials, CancellationToken cancellationToken);

    /// <summary>
    /// Completed deposits, withdrawals and transfers across the boundary of what <see cref="GetSnapshotAsync"/> reads,
    /// since a moment. Needed so that they are not counted as performance. Throws <see cref="VenueException"/>.
    /// </summary>
    Task<IReadOnlyList<VenueTransfer>> GetTransfersAsync(VenueCredentials credentials, DateTimeOffset since, CancellationToken cancellationToken);
}

/// <summary>Encrypts venue keys for storage. Unavailable when the master key is not configured.</summary>
public interface ICredentialVault
{
    bool IsAvailable { get; }

    string Protect(VenueCredentials credentials);

    VenueCredentials Unprotect(string protectedCredentials);
}

public sealed class VenueAdapterRegistry(IEnumerable<IVenueAdapter> adapters)
{
    private readonly Dictionary<Venue, IVenueAdapter> _adapters = adapters.ToDictionary(a => a.Venue);

    public IReadOnlyCollection<IVenueAdapter> All => _adapters.Values;

    public IVenueAdapter? Find(Venue venue) => _adapters.GetValueOrDefault(venue);
}
