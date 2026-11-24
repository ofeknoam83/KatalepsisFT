using System.Globalization;
using KatalepsisFT.Analytics.Performance;
using KatalepsisFT.Application.Alerts;
using KatalepsisFT.Domain.Positions;
using Microsoft.EntityFrameworkCore;

namespace KatalepsisFT.Application.History;

/// <summary>What a manager's imported track record holds, and how it meets the live readings that follow it.</summary>
/// <param name="TotalReturn">Return over the imported days, with money moved in or out taken into account.</param>
/// <param name="FirstLiveDay">The first day the manager was read from its accounts or hand-entered positions.</param>
/// <param name="JoinReturn">The first live NAV against the last imported one. A large figure means the two do not describe the same book.</param>
public sealed record TrackRecordDto(
    int ManagerId,
    string Manager,
    bool CanEdit,
    int Days,
    DateOnly? From,
    DateOnly? To,
    decimal? FirstNavUsd,
    decimal? LastNavUsd,
    double? TotalReturn,
    DateOnly? FirstLiveDay,
    decimal? FirstLiveNavUsd,
    double? JoinReturn
);

public sealed record TrackRecordChange(bool Done, IReadOnlyList<string> Errors, bool NotFound = false)
{
    public static TrackRecordChange Ok { get; } = new(true, []);

    public static TrackRecordChange Missing { get; } = new(false, [], true);

    public static TrackRecordChange Refused(params string[] errors) => new(false, errors);
}

/// <summary>
/// A manager's history from before this system read it: one NAV per day, and the money that moved in or out. It
/// gives a manager that has been running for years its performance, drawdown and volatility from the first day here.
/// Imported days come before the first live reading and never replace one.
/// </summary>
public sealed class TrackRecordService(IFtDbContext database, AlertEvaluator alerts, TimeProvider clock)
{
    public const string CsvHeader = "date,nav,flow";
    public const int MaxRows = 10_000;
    private const string FlowPrefix = "import:";
    private const decimal MaxAmount = 1_000_000_000_000m;
    private const int MaxErrors = 20;

    public async Task<TrackRecordDto?> GetAsync(int managerId, CancellationToken cancellationToken)
    {
        var manager = await database.ManagerAccounts.Where(m => m.Id == managerId).Select(m => new { m.Name, m.Sleeve.Fund.IsDemo }).FirstOrDefaultAsync(cancellationToken);
        if (manager is null)
        {
            return null;
        }

        var navs = await database.NavSnapshots.AsNoTracking().Where(n => n.ManagerAccountId == managerId).OrderBy(n => n.Date).Select(n => new { n.Date, n.NavUsd, n.IsImported }).ToListAsync(cancellationToken);
        var imported = navs.Where(n => n.IsImported).ToList();
        var firstLive = navs.FirstOrDefault(n => !n.IsImported);
        if (imported.Count == 0)
        {
            return new TrackRecordDto(managerId, manager.Name, !manager.IsDemo, 0, null, null, null, null, null, firstLive?.Date, firstLive?.NavUsd, null);
        }

        Dictionary<DateOnly, decimal> flows = (
            await database.CashFlows.AsNoTracking().Where(f => f.ManagerAccountId == managerId && f.ExternalId != null && f.ExternalId.StartsWith(FlowPrefix)).Select(f => new { f.Date, f.AmountUsd }).ToListAsync(cancellationToken)
        ).ToDictionary(f => f.Date, f => f.AmountUsd);
        double[] returns = ReturnCalculator.DailyReturns(imported.Select(n => n.NavUsd).ToList(), imported.Select(n => flows.GetValueOrDefault(n.Date)).ToList());
        decimal last = imported[^1].NavUsd;
        return new TrackRecordDto(
            managerId,
            manager.Name,
            !manager.IsDemo,
            imported.Count,
            imported[0].Date,
            imported[^1].Date,
            imported[0].NavUsd,
            last,
            (double)ReturnCalculator.GrowthIndex(returns)[^1] - 1.0,
            firstLive?.Date,
            firstLive?.NavUsd,
            firstLive is not null && last > 0m ? (double)(firstLive.NavUsd / last) - 1.0 : null
        );
    }

    /// <summary>Replaces the manager's imported history with the file's days, or changes nothing and says which lines are wrong.</summary>
    public async Task<TrackRecordChange> ImportAsync(int managerId, string csv, CancellationToken cancellationToken)
    {
        var manager = await database.ManagerAccounts.Include(m => m.Sleeve).ThenInclude(s => s.Fund).FirstOrDefaultAsync(m => m.Id == managerId, cancellationToken);
        if (manager is null)
        {
            return TrackRecordChange.Missing;
        }

        if (manager.Sleeve.Fund.IsDemo)
        {
            return TrackRecordChange.Refused("The example fund is rebuilt from its definition and cannot be edited.");
        }

        (List<(int Line, DateOnly Date, decimal Nav, decimal Flow)> rows, List<string> errors) = Parse(csv);
        DateOnly today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        DateOnly? firstLive = await database.NavSnapshots.Where(n => n.ManagerAccountId == managerId && !n.IsImported).OrderBy(n => n.Date).Select(n => (DateOnly?)n.Date).FirstOrDefaultAsync(cancellationToken);
        HashSet<DateOnly> seen = [];
        foreach ((int line, DateOnly date, decimal nav, decimal flow) in rows)
        {
            if (errors.Count >= MaxErrors)
            {
                break;
            }

            if (!seen.Add(date))
            {
                errors.Add($"Line {line}: {date:yyyy-MM-dd} appears more than once.");
            }
            else if (firstLive is DateOnly live && date >= live)
            {
                errors.Add($"Line {line}: {date:yyyy-MM-dd} is on or after {live:yyyy-MM-dd}, the first day this manager was read live. A track record ends before that day.");
            }
            else if (date >= today)
            {
                errors.Add($"Line {line}: {date:yyyy-MM-dd} is not in the past. Today's value comes from the manager's accounts or positions.");
            }
            else if (date.Year < 2000)
            {
                errors.Add($"Line {line}: {date:yyyy-MM-dd} is too long ago to be a date of this manager.");
            }

            if (nav <= 0m || nav > MaxAmount)
            {
                errors.Add($"Line {line}: the NAV must be above zero and at most {MaxAmount:N0}.");
            }

            if (Math.Abs(flow) > MaxAmount)
            {
                errors.Add($"Line {line}: the flow is out of range.");
            }
        }

        if (errors.Count == 0 && rows.Count < 2)
        {
            errors.Add("A track record needs at least two days.");
        }

        if (errors.Count > 0)
        {
            return new TrackRecordChange(false, errors.Take(MaxErrors).ToList());
        }

        // Rows that stay are changed where they stand; a day or a flow is only added or removed when the file says so.
        Dictionary<DateOnly, NavSnapshot> oldNavs = await database.NavSnapshots.Where(n => n.ManagerAccountId == managerId && n.IsImported).ToDictionaryAsync(n => n.Date, cancellationToken);
        Dictionary<DateOnly, CashFlow> oldFlows = await database.CashFlows.Where(f => f.ManagerAccountId == managerId && f.ExternalId != null && f.ExternalId.StartsWith(FlowPrefix)).ToDictionaryAsync(f => f.Date, cancellationToken);
        foreach ((_, DateOnly date, decimal nav, decimal flow) in rows)
        {
            if (oldNavs.Remove(date, out NavSnapshot? existing))
            {
                existing.NavUsd = nav;
            }
            else
            {
                database.NavSnapshots.Add(new NavSnapshot { ManagerAccountId = managerId, Date = date, NavUsd = nav, IsImported = true });
            }

            oldFlows.Remove(date, out CashFlow? oldFlow);
            if (flow == 0m)
            {
                if (oldFlow is not null)
                {
                    database.CashFlows.Remove(oldFlow);
                }

                continue;
            }

            CashFlow kept = oldFlow ?? database.CashFlows.Add(new CashFlow { ManagerAccountId = managerId, Date = date, ExternalId = $"{FlowPrefix}{date:yyyy-MM-dd}" }).Entity;
            kept.AmountUsd = flow;
            kept.Kind = flow >= 0m ? CashFlowKind.Subscription : CashFlowKind.Redemption;
            kept.Note = "Imported with the track record";
        }

        database.NavSnapshots.RemoveRange(oldNavs.Values);
        database.CashFlows.RemoveRange(oldFlows.Values);
        DateOnly first = rows.Min(r => r.Date);
        if (first < manager.InceptionDate)
        {
            manager.InceptionDate = first;
        }

        await database.SaveChangesAsync(cancellationToken);
        await alerts.EvaluateAsync(manager.Sleeve.FundId, cancellationToken);
        return TrackRecordChange.Ok;
    }

    public async Task<TrackRecordChange> RemoveAsync(int managerId, CancellationToken cancellationToken)
    {
        var manager = await database.ManagerAccounts.Include(m => m.Sleeve).ThenInclude(s => s.Fund).FirstOrDefaultAsync(m => m.Id == managerId, cancellationToken);
        if (manager is null)
        {
            return TrackRecordChange.Missing;
        }

        if (manager.Sleeve.Fund.IsDemo)
        {
            return TrackRecordChange.Refused("The example fund is rebuilt from its definition and cannot be edited.");
        }

        database.NavSnapshots.RemoveRange(await database.NavSnapshots.Where(n => n.ManagerAccountId == managerId && n.IsImported).ToListAsync(cancellationToken));
        database.CashFlows.RemoveRange(await database.CashFlows.Where(f => f.ManagerAccountId == managerId && f.ExternalId != null && f.ExternalId.StartsWith(FlowPrefix)).ToListAsync(cancellationToken));
        await database.SaveChangesAsync(cancellationToken);
        // The manager now starts where its live readings do, or today if there are none yet.
        DateOnly? firstLive = await database.NavSnapshots.Where(n => n.ManagerAccountId == managerId).OrderBy(n => n.Date).Select(n => (DateOnly?)n.Date).FirstOrDefaultAsync(cancellationToken);
        manager.InceptionDate = firstLive ?? DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        await database.SaveChangesAsync(cancellationToken);
        await alerts.EvaluateAsync(manager.Sleeve.FundId, cancellationToken);
        return TrackRecordChange.Ok;
    }

    /// <summary>
    /// Reads "date,nav,flow" lines. The flow column may be left out or left empty. Dates are year-month-day; comma or
    /// semicolon separates, and thousands separators inside quotes are accepted.
    /// </summary>
    public static (List<(int Line, DateOnly Date, decimal Nav, decimal Flow)> Rows, List<string> Errors) Parse(string csv)
    {
        List<(int, DateOnly, decimal, decimal)> rows = [];
        List<string> errors = [];
        string[] lines = csv.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimStart('﻿').Split('\n');
        int headerAt = Array.FindIndex(lines, l => l.Trim().Length > 0);
        if (headerAt < 0)
        {
            return (rows, ["The file is empty."]);
        }

        char separator = lines[headerAt].Contains(';') && !lines[headerAt].Contains(',') ? ';' : ',';
        List<string> header = lines[headerAt].Split(separator).Select(h => h.Trim().Trim('"').ToLowerInvariant()).ToList();
        int dateAt = header.IndexOf("date"),
            navAt = header.IndexOf("nav"),
            flowAt = header.IndexOf("flow");
        if (dateAt < 0 || navAt < 0)
        {
            return (rows, [$"The first line must name the columns: {CsvHeader} (flow may be left out)."]);
        }

        for (int i = headerAt + 1; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0)
            {
                continue;
            }

            if (rows.Count >= MaxRows)
            {
                errors.Add($"The file has more than {MaxRows:N0} days.");
                break;
            }

            if (errors.Count >= MaxErrors)
            {
                break;
            }

            List<string> cells = Cells(lines[i], separator);
            string Cell(int at) => at >= 0 && at < cells.Count ? cells[at].Trim() : string.Empty;
            int line = i + 1;
            if (cells.Count > header.Count)
            {
                // Usually an amount written with thousands separators and no quotes: 1,250,000.00 is three cells.
                errors.Add($"Line {line} has {cells.Count} values but the heading has {header.Count}. An amount with thousands separators has to be in double quotes, or written without them.");
                continue;
            }

            if (!DateOnly.TryParseExact(Cell(dateAt), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
            {
                errors.Add($"Line {line}: \"{Cell(dateAt)}\" is not a date written as year-month-day, like 2025-03-31.");
                continue;
            }

            if (!Number(Cell(navAt), separator, out decimal nav))
            {
                errors.Add($"Line {line}: \"{Cell(navAt)}\" is not a NAV in USD.");
                continue;
            }

            decimal flow = 0m;
            if (Cell(flowAt).Length > 0 && !Number(Cell(flowAt), separator, out flow))
            {
                errors.Add($"Line {line}: \"{Cell(flowAt)}\" is not an amount. Leave the flow empty when no money moved.");
                continue;
            }

            rows.Add((line, date, Math.Round(nav, 2), Math.Round(flow, 2)));
        }

        return (rows.OrderBy(r => r.Item2).ToList(), errors);
    }

    /// <summary>
    /// Reads an amount. In a file separated by semicolons the comma is the decimal mark, as European spreadsheets write
    /// it (1.234.567,89); otherwise the comma separates thousands (1,234,567.89).
    /// </summary>
    private static bool Number(string text, char separator, out decimal value)
    {
        string plain = text.Replace("$", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal).Trim();
        plain = separator == ';' && plain.Contains(',') ? plain.Replace(".", string.Empty, StringComparison.Ordinal).Replace(',', '.') : plain.Replace(",", string.Empty, StringComparison.Ordinal);
        return decimal.TryParse(plain, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Splits one line, keeping a separator that sits inside double quotes.</summary>
    private static List<string> Cells(string line, char separator)
    {
        List<string> cells = [];
        System.Text.StringBuilder cell = new();
        bool quoted = false;
        foreach (char c in line)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (c == separator && !quoted)
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else
            {
                cell.Append(c);
            }
        }

        cells.Add(cell.ToString());
        return cells;
    }
}
