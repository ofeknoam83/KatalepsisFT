using System.Net;
using System.Net.Mail;
using FluentValidation;
using KatalepsisFT.Domain.Reports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KatalepsisFT.Application.Reports;

public sealed record MailAttachment(string FileName, string ContentType, byte[] Content);

public sealed record MailToSend(IReadOnlyList<string> To, string Subject, string HtmlBody, IReadOnlyList<MailAttachment> Attachments);

/// <summary>Sends email. Whether it can is a matter of how the server is set up, so callers ask first.</summary>
public interface IMailSender
{
    /// <summary>False until the server has been given a mail account to send from.</summary>
    bool IsConfigured { get; }

    /// <summary>Throws <see cref="MailException"/> with a reason that is safe to show.</summary>
    Task SendAsync(MailToSend mail, CancellationToken cancellationToken);
}

public sealed class MailException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record ReportScheduleDto(
    int Id,
    ReportKind Kind,
    ReportFrequency Frequency,
    int HourUtc,
    string Recipients,
    bool IsEnabled,
    DateOnly? LastSentOn,
    DateTimeOffset? LastAttemptAt,
    string? LastError,
    string CreatedBy
);

public sealed record ReportSchedulesDto(bool MailConfigured, IReadOnlyList<ReportScheduleDto> Schedules);

public sealed record SaveReportScheduleRequest(ReportKind Kind, ReportFrequency Frequency, int HourUtc, string Recipients, bool IsEnabled);

public sealed class SaveReportScheduleRequestValidator : AbstractValidator<SaveReportScheduleRequest>
{
    public const int MaxRecipients = 20;

    public SaveReportScheduleRequestValidator()
    {
        RuleFor(x => x.Kind).IsInEnum();
        RuleFor(x => x.Frequency).IsInEnum();
        RuleFor(x => x.HourUtc).InclusiveBetween(0, 23).WithMessage("The hour is between 0 and 23, UTC.");
        RuleFor(x => x.Recipients)
            .Must(text => ReportScheduleService.Addresses(text) is { Count: > 0 and <= MaxRecipients })
            .WithMessage($"Give one to {MaxRecipients} email addresses, separated by commas.");
    }
}

public sealed record ScheduleChange(bool Done, string? Error, bool NotFound = false)
{
    public static ScheduleChange Ok { get; } = new(true, null);

    public static ScheduleChange Missing { get; } = new(false, null, true);

    public static ScheduleChange Refused(string error) => new(false, error);
}

/// <summary>Reports that go out by email on a rhythm: who gets which, when, and the sending itself.</summary>
public sealed class ReportScheduleService(IFtDbContext database, ReportService reports, IMailSender mail, TimeProvider clock, ILogger<ReportScheduleService> logger)
{
    /// <summary>A schedule that failed is tried again no sooner than this.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(15);

    /// <summary>The addresses in a comma-separated list; null when any of them is not an address.</summary>
    public static List<string>? Addresses(string? text)
    {
        List<string> parts = (text ?? string.Empty).Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return parts.All(p => p.Length <= 254 && !p.Contains(' ') && MailAddress.TryCreate(p, out MailAddress? address) && address.Address == p && address.Host.Contains('.')) ? parts : null;
    }

    public async Task<ReportSchedulesDto?> ListAsync(int fundId, CancellationToken cancellationToken)
    {
        if (!await database.Funds.AnyAsync(f => f.Id == fundId, cancellationToken))
        {
            return null;
        }

        List<ReportSchedule> schedules = await database.ReportSchedules.AsNoTracking().Where(s => s.FundId == fundId).OrderBy(s => s.Id).ToListAsync(cancellationToken);
        return new ReportSchedulesDto(mail.IsConfigured, schedules.Select(Dto).ToList());
    }

    public async Task<ScheduleChange> AddAsync(int fundId, SaveReportScheduleRequest request, string createdBy, CancellationToken cancellationToken)
    {
        if (!await database.Funds.AnyAsync(f => f.Id == fundId, cancellationToken))
        {
            return ScheduleChange.Missing;
        }

        database.ReportSchedules.Add(
            new ReportSchedule
            {
                FundId = fundId,
                Kind = request.Kind,
                Frequency = request.Frequency,
                HourUtc = request.HourUtc,
                Recipients = string.Join(", ", Addresses(request.Recipients)!),
                IsEnabled = request.IsEnabled,
                CreatedBy = createdBy,
                CreatedAt = clock.GetUtcNow(),
            }
        );
        await database.SaveChangesAsync(cancellationToken);
        return ScheduleChange.Ok;
    }

    public async Task<ScheduleChange> SaveAsync(int scheduleId, SaveReportScheduleRequest request, CancellationToken cancellationToken)
    {
        ReportSchedule? schedule = await database.ReportSchedules.FirstOrDefaultAsync(s => s.Id == scheduleId, cancellationToken);
        if (schedule is null)
        {
            return ScheduleChange.Missing;
        }

        schedule.Kind = request.Kind;
        schedule.Frequency = request.Frequency;
        schedule.HourUtc = request.HourUtc;
        schedule.Recipients = string.Join(", ", Addresses(request.Recipients)!);
        schedule.IsEnabled = request.IsEnabled;
        await database.SaveChangesAsync(cancellationToken);
        return ScheduleChange.Ok;
    }

    public async Task<ScheduleChange> DeleteAsync(int scheduleId, CancellationToken cancellationToken)
    {
        ReportSchedule? schedule = await database.ReportSchedules.FirstOrDefaultAsync(s => s.Id == scheduleId, cancellationToken);
        if (schedule is null)
        {
            return ScheduleChange.Missing;
        }

        database.ReportSchedules.Remove(schedule);
        await database.SaveChangesAsync(cancellationToken);
        return ScheduleChange.Ok;
    }

    /// <summary>Sends the report now, whatever its rhythm says. The reason is returned when it could not go out.</summary>
    public async Task<ScheduleChange> SendNowAsync(int scheduleId, CancellationToken cancellationToken)
    {
        ReportSchedule? schedule = await database.ReportSchedules.FirstOrDefaultAsync(s => s.Id == scheduleId, cancellationToken);
        if (schedule is null)
        {
            return ScheduleChange.Missing;
        }

        string? error = await SendAsync(schedule, cancellationToken);
        return error is null ? ScheduleChange.Ok : ScheduleChange.Refused(error);
    }

    /// <summary>Whether a schedule should go out at this moment: its day has come, its hour has passed, and it has not gone out today.</summary>
    public static bool IsDue(ReportSchedule schedule, DateTimeOffset now)
    {
        DateOnly today = DateOnly.FromDateTime(now.UtcDateTime);
        bool rightDay = schedule.Frequency switch
        {
            ReportFrequency.Weekly => today.DayOfWeek == DayOfWeek.Monday,
            ReportFrequency.Monthly => today.Day == 1,
            _ => true,
        };
        return schedule.IsEnabled
            && rightDay
            && now.UtcDateTime.Hour >= schedule.HourUtc
            && schedule.LastSentOn != today
            && (schedule.LastAttemptAt is null || schedule.LastError is null || now - schedule.LastAttemptAt.Value >= RetryAfter);
    }

    /// <summary>Sends every report that is due. Called every minute by the worker.</summary>
    /// <returns>How many went out.</returns>
    public async Task<int> RunDueAsync(CancellationToken cancellationToken)
    {
        if (!mail.IsConfigured)
        {
            return 0;
        }

        DateTimeOffset now = clock.GetUtcNow();
        List<ReportSchedule> enabled = await database.ReportSchedules.Where(s => s.IsEnabled).ToListAsync(cancellationToken);
        int sent = 0;
        foreach (ReportSchedule schedule in enabled.Where(s => IsDue(s, now)))
        {
            if (await SendAsync(schedule, cancellationToken) is null)
            {
                sent++;
            }
        }

        return sent;
    }

    /// <returns>Null when the report went out, otherwise why not.</returns>
    private async Task<string?> SendAsync(ReportSchedule schedule, CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();
        schedule.LastAttemptAt = now;
        string? error = null;
        try
        {
            if (!mail.IsConfigured)
            {
                throw new MailException("The server has no mail account to send from yet.");
            }

            ReportFile? report = schedule.Kind == ReportKind.Factsheet ? await reports.FactsheetAsync(schedule.FundId, cancellationToken) : await reports.DailyNavAsync(schedule.FundId, cancellationToken);
            if (report is null)
            {
                throw new MailException("The fund has no valued positions to report on yet.");
            }

            // The factsheet is its own page; opened from the attachment it prints to a PDF. Mail programs show the same page inline, some without the chart.
            string body = schedule.Kind == ReportKind.Factsheet
                ? System.Text.Encoding.UTF8.GetString(report.Content)
                : $"<p style=\"font:14px -apple-system,'Segoe UI',Arial,sans-serif\">{WebUtility.HtmlEncode(report.Summary)}</p><p style=\"font:12px -apple-system,'Segoe UI',Arial,sans-serif;color:#8c8c8c\">Sent by Katalepsis FT on its schedule. Figures are unaudited.</p>";
            await mail.SendAsync(new MailToSend(Addresses(schedule.Recipients) ?? [], report.Subject, body, [new MailAttachment(report.FileName, report.ContentType, report.Content)]), cancellationToken);
            schedule.LastSentOn = DateOnly.FromDateTime(now.UtcDateTime);
            schedule.LastError = null;
        }
        catch (MailException e)
        {
            error = e.Message;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Report schedule {ScheduleId} could not be sent.", schedule.Id);
            error = $"The report could not be produced ({e.GetType().Name}). The server log has the details.";
        }

        if (error is not null)
        {
            schedule.LastError = error.Length <= 500 ? error : error[..500];
            logger.LogWarning("Report schedule {ScheduleId} was not sent: {Reason}", schedule.Id, error);
        }

        await database.SaveChangesAsync(cancellationToken);
        return error;
    }

    private static ReportScheduleDto Dto(ReportSchedule s) => new(s.Id, s.Kind, s.Frequency, s.HourUtc, s.Recipients, s.IsEnabled, s.LastSentOn, s.LastAttemptAt, s.LastError, s.CreatedBy);
}
