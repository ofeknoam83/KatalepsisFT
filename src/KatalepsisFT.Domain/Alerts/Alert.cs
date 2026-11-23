namespace KatalepsisFT.Domain.Alerts;

public enum AlertStatus
{
    Open = 1,
    Acknowledged = 2,
    Resolved = 3,
}

public enum AlertActionKind
{
    Raised = 1,
    Acknowledged = 2,
    Assigned = 3,
    Commented = 4,
    Snoozed = 5,
    Resolved = 6,

    /// <summary>The condition stopped holding; recorded by the system.</summary>
    Recovered = 7,
}

/// <summary>One occurrence of a rule's condition, from the evaluation that first saw it until it recovers or is resolved.</summary>
public class Alert
{
    public int Id { get; set; }
    public int RuleId { get; set; }
    public AlertRule Rule { get; set; } = null!;
    public AlertStatus Status { get; set; }

    /// <summary>The asset or manager the figure is about, when the metric picks one.</summary>
    public string? Subject { get; set; }

    /// <summary>The figure at the latest evaluation that saw the condition.</summary>
    public decimal Value { get; set; }

    /// <summary>The figure at its furthest past the limit while this alert was active: the size of the breach.</summary>
    public decimal PeakValue { get; set; }

    /// <summary>The rule's threshold when the alert was last evaluated.</summary>
    public decimal Threshold { get; set; }
    public DateOnly DataDate { get; set; }
    public DateTimeOffset RaisedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }

    /// <summary>
    /// True from the moment a person resolves the alert until the condition stops holding. While true the rule raises
    /// no new alert, so resolving something by hand does not bring it straight back.
    /// </summary>
    public bool ResolvedWhileBreaching { get; set; }
    public int? AssigneeUserId { get; set; }
    public string? AssigneeName { get; set; }
    public DateTimeOffset? SnoozedUntil { get; set; }
    public List<AlertAction> Actions { get; set; } = [];

    public bool IsActive => Status != AlertStatus.Resolved;

    public AlertAction Record(AlertActionKind kind, DateTimeOffset at, int? userId, string? userName, string? note = null)
    {
        AlertAction action = new() { Kind = kind, At = at, UserId = userId, UserName = userName, Note = note };
        Actions.Add(action);
        return action;
    }
}

/// <summary>The audit trail of an alert. A null user is the system.</summary>
public class AlertAction
{
    public int Id { get; set; }
    public int AlertId { get; set; }
    public Alert Alert { get; set; } = null!;
    public AlertActionKind Kind { get; set; }
    public DateTimeOffset At { get; set; }
    public int? UserId { get; set; }
    public string? UserName { get; set; }
    public string? Note { get; set; }
}
