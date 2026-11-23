using KatalepsisFT.Domain.Funds;

namespace KatalepsisFT.Domain.Reports;

public enum ReportKind
{
    /// <summary>The workbook of NAV per manager per day.</summary>
    DailyNav = 1,

    /// <summary>The one-page summary of the fund.</summary>
    Factsheet = 2,
}

public enum ReportFrequency
{
    Daily = 1,

    /// <summary>Every Monday.</summary>
    Weekly = 2,

    /// <summary>On the first day of each month.</summary>
    Monthly = 3,
}

/// <summary>A report of one fund that is emailed to a list of people on a fixed rhythm.</summary>
public class ReportSchedule
{
    public int Id { get; set; }
    public int FundId { get; set; }
    public Fund Fund { get; set; } = null!;
    public ReportKind Kind { get; set; }
    public ReportFrequency Frequency { get; set; }

    /// <summary>The hour of the day, UTC, from which the report may go out.</summary>
    public int HourUtc { get; set; }

    /// <summary>Email addresses, separated by commas.</summary>
    public required string Recipients { get; set; }
    public bool IsEnabled { get; set; } = true;

    /// <summary>The UTC day the report last went out, on schedule or by hand.</summary>
    public DateOnly? LastSentOn { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }

    /// <summary>Why the last attempt failed; null when it went out.</summary>
    public string? LastError { get; set; }
    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
