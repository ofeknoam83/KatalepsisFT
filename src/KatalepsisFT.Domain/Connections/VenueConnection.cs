using KatalepsisFT.Domain.Funds;

namespace KatalepsisFT.Domain.Connections;

public enum Venue
{
    Binance = 1,
    Bybit = 2,
    Okx = 3,
    Deribit = 4,
    CoinbaseExchange = 5,
    CoinbasePrime = 6,
    Kucoin = 7,
    Mexc = 8,
}

public enum ConnectionState
{
    /// <summary>Saved, not read yet.</summary>
    Pending = 1,

    /// <summary>The latest read succeeded.</summary>
    Live = 2,

    /// <summary>The latest read failed; the manager's book keeps the last complete snapshot.</summary>
    Failed = 3,

    /// <summary>Switched off by an Admin; not read.</summary>
    Disabled = 4,
}

/// <summary>A read-only API key of one manager on one venue. A manager can have several.</summary>
public class VenueConnection
{
    public int Id { get; set; }
    public int ManagerAccountId { get; set; }
    public ManagerAccount ManagerAccount { get; set; } = null!;
    public Venue Venue { get; set; }
    public required string Label { get; set; }

    /// <summary>The key, secret and passphrase, encrypted with the master key. Never leaves the server.</summary>
    public required string EncryptedCredentials { get; set; }

    /// <summary>The last four characters of the API key, to tell keys apart on screen.</summary>
    public required string KeyHint { get; set; }

    /// <summary>False when the venue could not tell us what the key is allowed to do.</summary>
    public bool PermissionsVerified { get; set; }
    public ConnectionState State { get; set; } = ConnectionState.Pending;
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public string? LastError { get; set; }
    public int ConsecutiveFailures { get; set; }

    /// <summary>The latest complete snapshot, normalised, as JSON. A manager's book is the sum over its connections.</summary>
    public string? LastSnapshotJson { get; set; }
    /// <summary>When deposits and withdrawals were last read successfully.</summary>
    public DateTimeOffset? TransfersCheckedAt { get; set; }

    /// <summary>Why deposits and withdrawals could not be read; positions are unaffected.</summary>
    public string? TransfersError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}
