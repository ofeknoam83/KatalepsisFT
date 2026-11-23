namespace KatalepsisFT.Domain.Audit;

/// <summary>
/// One change somebody made: who, when, what, and to what. Entries are only ever added. Names are copied in as they
/// were at the time, so an entry still reads correctly after the thing it is about is renamed or removed.
/// </summary>
public class AuditEntry
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }

    /// <summary>The signed-in user's name, or the email that was typed for a sign-in that failed.</summary>
    public required string UserName { get; set; }

    /// <summary>What was done, in a few words: "Manager retired", "Key added".</summary>
    public required string Action { get; set; }

    /// <summary>The fund it concerns; null for things that belong to no fund, such as signing in.</summary>
    public int? FundId { get; set; }
    public string? FundName { get; set; }

    /// <summary>What it was done to: "Directional / Alpha Desk".</summary>
    public string? Subject { get; set; }

    /// <summary>What was sent, with secrets left out.</summary>
    public string? Detail { get; set; }
}
