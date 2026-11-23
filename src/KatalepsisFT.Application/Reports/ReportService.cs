using System.Globalization;
using System.Net;
using System.Text;
using ClosedXML.Excel;
using KatalepsisFT.Analytics.Holdings;
using KatalepsisFT.Application.Alerts;
using KatalepsisFT.Application.Holdings;
using KatalepsisFT.Application.Performance;
using KatalepsisFT.Application.Risk;
using KatalepsisFT.Domain.Funds;
using Microsoft.EntityFrameworkCore;

namespace KatalepsisFT.Application.Reports;

/// <summary>A finished report: a file, and the line that introduces it in an email.</summary>
public sealed record ReportFile(string FileName, string ContentType, byte[] Content, string Subject, string Summary);

/// <summary>
/// The two documents a fund sends out. The factsheet is one self-contained page (it prints to a PDF from any
/// browser); the daily NAV sheet is a workbook of NAV per manager per day, today's books and recent cash flows.
/// Both are built from the same figures the screens show.
/// </summary>
public sealed class ReportService(IFtDbContext database, PerformanceService performance, VenueExposureService venues, AlertService alerts, TimeProvider clock)
{
    /// <summary>Days of history in the NAV workbook.</summary>
    public const int NavDays = 120;

    private static readonly CultureInfo Plain = CultureInfo.InvariantCulture;

    // ---------------------------------------------------------------- factsheet

    /// <returns>Null when the fund does not exist or has never been valued.</returns>
    public async Task<ReportFile?> FactsheetAsync(int fundId, CancellationToken cancellationToken)
    {
        FundOverviewDto? overview = await new GetFundOverviewHandler(database).Handle(new GetFundOverviewQuery(fundId, InstrumentFilter.All), cancellationToken);
        PerformanceDto? ytd = await performance.GetAsync(PerformanceScope.Fund, fundId, null, PerformancePeriod.Ytd, cancellationToken);
        if (overview is null || ytd is null)
        {
            return null;
        }

        VenueExposureDto? held = await venues.GetAsync(fundId, cancellationToken);
        DateOnly today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        BreachRegisterDto? breaches = await alerts.BreachRegisterAsync(fundId, today.AddDays(-30), today, cancellationToken);
        string html = FactsheetHtml(overview, ytd, held, breaches, clock.GetUtcNow());
        string name = overview.Fund.Name;
        return new ReportFile(
            $"{Slug(name)}-factsheet-{overview.AsOf:yyyy-MM-dd}.html",
            "text/html; charset=utf-8",
            Encoding.UTF8.GetBytes(html),
            $"{name}: factsheet as of {overview.AsOf:d MMM yyyy}",
            $"NAV {Usd(overview.Totals.NavUsd)}, {Pct(ytd.Stats.TotalReturn)} year to date against {Pct(ytd.BenchmarkStats.TotalReturn)} for {ytd.Benchmark.Name}."
        );
    }

    private static string FactsheetHtml(FundOverviewDto o, PerformanceDto p, VenueExposureDto? held, BreachRegisterDto? breaches, DateTimeOffset generated)
    {
        StringBuilder h = new();
        h.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        h.Append("<title>").Append(E(o.Fund.Name)).Append(" factsheet</title><style>");
        h.Append(
            "body{font:13px/1.45 -apple-system,'Segoe UI',Inter,Arial,sans-serif;color:#1f1f1f;margin:0;background:#f5f6f8}"
                + ".sheet{max-width:920px;margin:0 auto;background:#fff;padding:28px 32px}"
                + "h1{font-size:22px;margin:0}h2{font-size:14px;margin:22px 0 8px;border-bottom:1px solid #e5e5e5;padding-bottom:4px}"
                + ".sub{color:#8c8c8c;margin:2px 0 16px}.tiles{display:flex;gap:10px;flex-wrap:wrap}"
                + ".tile{flex:1 1 150px;border:1px solid #ececec;border-radius:6px;padding:8px 12px}.tile .k{color:#8c8c8c;font-size:11px}.tile .v{font-size:18px;font-weight:600}.tile .d{color:#595959;font-size:11px}"
                + "table{border-collapse:collapse;width:100%}th,td{padding:5px 8px;border-bottom:1px solid #f0f0f0;text-align:left}th{font-size:11px;color:#595959;font-weight:600;background:#fafafa}"
                + ".n{text-align:right;font-variant-numeric:tabular-nums;white-space:nowrap}.up{color:#006300}.down{color:#cf1322}.total td{font-weight:600;background:#fafafa}"
                + ".foot{color:#8c8c8c;font-size:11px;margin-top:22px}.tag{display:inline-block;border:1px solid #ffe58f;background:#fffbe6;color:#ad6800;border-radius:4px;padding:0 6px;font-size:11px;margin-left:8px;vertical-align:middle}"
                + ".print{float:right;font:inherit;padding:4px 12px;border:1px solid #d9d9d9;border-radius:4px;background:#fff;cursor:pointer}"
                + "@media print{body{background:#fff}.sheet{padding:0;max-width:none}.print{display:none}h2{break-after:avoid}table{break-inside:auto}tr{break-inside:avoid}}@page{size:A4;margin:14mm}"
        );
        h.Append("</style></head><body><div class=\"sheet\">");
        h.Append("<button class=\"print\" onclick=\"window.print()\">Print or save as PDF</button>");
        h.Append("<h1>").Append(E(o.Fund.Name));
        if (o.Fund.IsDemo)
        {
            h.Append("<span class=\"tag\">Example fund</span>");
        }

        h.Append("</h1><div class=\"sub\">Factsheet as of ").Append(o.AsOf.ToString("d MMMM yyyy", Plain)).Append(" (UTC) · benchmark ").Append(E(p.Benchmark.Name)).Append("</div>");

        h.Append("<div class=\"tiles\">");
        Tile(h, "Net asset value", Usd(o.Totals.NavUsd), $"Cash {Usd(o.Totals.CashUsd)}");
        Tile(h, "Return, year to date", Pct(p.Stats.TotalReturn), $"{E(p.Benchmark.Name)} {Pct(p.BenchmarkStats.TotalReturn)}", p.Stats.TotalReturn);
        Tile(h, "Excess over benchmark", Pct(p.Comparison.ExcessReturn), p.Comparison.Beta is double beta ? $"Beta {beta.ToString("0.00", Plain)}" : "Beta –", p.Comparison.ExcessReturn);
        Tile(h, "Volatility, annualised", Share(p.Stats.Volatility), p.Stats.Sharpe is double sharpe ? $"Sharpe {sharpe.ToString("0.00", Plain)}" : "Sharpe –");
        Tile(h, "Maximum drawdown", Share(p.Stats.MaxDrawdown), $"{p.From:d MMM yyyy} to {p.AsOf:d MMM yyyy}");
        Tile(h, "Exposure, gross / net", $"{Ratio(o.Totals.GrossUsd, o.Totals.NavUsd)} / {Ratio(o.Totals.NetUsd, o.Totals.NavUsd)}", "of NAV");
        h.Append("</div>");

        h.Append("<h2>Cumulative return, year to date</h2>").Append(Chart(p));

        h.Append("<h2>Returns by period</h2><table><tr><th>Period</th><th class=\"n\">Fund</th><th class=\"n\">").Append(E(p.Benchmark.Name)).Append("</th><th class=\"n\">Excess</th></tr>");
        foreach (PeriodReturnDto period in p.Periods)
        {
            double? excess = period.Portfolio is double f && period.Benchmark is double b ? f - b : null;
            h.Append("<tr><td>").Append(E(period.Label)).Append("</td>").Append(Cell(period.Portfolio)).Append(Cell(period.Benchmark)).Append(Cell(excess)).Append("</tr>");
        }

        h.Append("</table>");

        h.Append("<h2>Sleeves</h2><table><tr><th>Sleeve</th><th class=\"n\">NAV</th><th class=\"n\">Share</th><th class=\"n\">Return YTD</th><th class=\"n\">Contribution</th><th class=\"n\">Long</th><th class=\"n\">Short</th><th class=\"n\">Gross</th><th class=\"n\">Net</th></tr>");
        foreach (SleeveRowDto sleeve in o.Sleeves)
        {
            ChildPerformanceDto? child = p.Children.FirstOrDefault(c => c.Kind == PerformanceScope.Sleeve && c.Id == sleeve.Sleeve.Id);
            h.Append("<tr><td><b>").Append(E(sleeve.Sleeve.Name)).Append("</b> · ").Append(sleeve.Managers).Append(sleeve.Managers == 1 ? " manager" : " managers").Append("</td>");
            h.Append(Money(sleeve.Totals.NavUsd)).Append(Plain_(Ratio(sleeve.Totals.NavUsd, o.Totals.NavUsd))).Append(Cell(child?.Stats.TotalReturn)).Append(Cell(child?.Contribution, 2));
            h.Append(Money(sleeve.Totals.LongUsd)).Append(Money(sleeve.Totals.ShortUsd)).Append(Plain_(Ratio(sleeve.Totals.GrossUsd, sleeve.Totals.NavUsd))).Append(Plain_(Ratio(sleeve.Totals.NetUsd, sleeve.Totals.NavUsd))).Append("</tr>");
        }

        h.Append("<tr class=\"total\"><td>Fund</td>").Append(Money(o.Totals.NavUsd)).Append(Plain_("100%")).Append(Cell(p.Stats.TotalReturn)).Append(Cell(p.Children.Sum(c => c.Contribution), 2));
        h.Append(Money(o.Totals.LongUsd)).Append(Money(o.Totals.ShortUsd)).Append(Plain_(Ratio(o.Totals.GrossUsd, o.Totals.NavUsd))).Append(Plain_(Ratio(o.Totals.NetUsd, o.Totals.NavUsd))).Append("</tr></table>");

        h.Append("<h2>Largest exposures</h2><table><tr><th>Asset</th><th class=\"n\">Long</th><th class=\"n\">Short</th><th class=\"n\">Net</th><th class=\"n\">Net ÷ NAV</th><th class=\"n\">Managers</th></tr>");
        foreach (AssetRowDto asset in o.TopAssets)
        {
            h.Append("<tr><td><b>").Append(E(asset.Asset.Symbol)).Append("</b></td>").Append(Money(asset.LongUsd)).Append(Money(asset.ShortUsd)).Append(Money(asset.NetUsd, true)).Append(Plain_(Ratio(asset.NetUsd, o.Totals.NavUsd))).Append(Plain_(asset.Holders.Count.ToString(Plain))).Append("</tr>");
        }

        h.Append("</table>");

        if (held is { Venues.Count: > 0 })
        {
            h.Append("<h2>Where the money sits</h2><table><tr><th>Held on</th><th class=\"n\">Value</th><th class=\"n\">Share of fund</th><th class=\"n\">Accounts</th></tr>");
            foreach (VenueRowDto venue in held.Venues)
            {
                h.Append("<tr><td><b>").Append(E(venue.Name)).Append("</b></td>").Append(Money(venue.NavUsd)).Append(Plain_((venue.Share * 100m).ToString("0.0", Plain) + "%")).Append(Plain_(venue.Accounts == 0 ? "–" : venue.Accounts.ToString(Plain))).Append("</tr>");
            }

            h.Append("</table>");
        }

        if (breaches is not null)
        {
            h.Append("<h2>Limits and rules, last 30 days</h2>");
            if (breaches.Rows.Count == 0)
            {
                h.Append("<p>No rule or mandate limit was broken.</p>");
            }
            else
            {
                h.Append("<p>").Append(breaches.Breaches).Append(breaches.Breaches == 1 ? " breach" : " breaches").Append(", ").Append(breaches.MandateBreaches).Append(" of mandate limits, ").Append(breaches.StillOpen).Append(" still open.</p>");
                h.Append("<table><tr><th>Raised (UTC)</th><th>Where</th><th>Rule</th><th class=\"n\">Peak</th><th>How it ended</th></tr>");
                foreach (BreachDto breach in breaches.Rows.Take(12))
                {
                    h.Append("<tr><td>").Append(breach.RaisedAt.UtcDateTime.ToString("d MMM HH:mm", Plain)).Append("</td><td>").Append(E(breach.ScopeName)).Append("</td><td>").Append(E(breach.Rule));
                    h.Append(breach.Subject is null ? string.Empty : " · " + E(breach.Subject)).Append("</td>").Append(Plain_(AlertText.Figure(breach.Metric, breach.PeakValue))).Append("<td>").Append(E(breach.Outcome)).Append("</td></tr>");
                }

                h.Append("</table>");
            }
        }

        h.Append("<div class=\"foot\">Produced by Katalepsis FT on ").Append(generated.UtcDateTime.ToString("d MMM yyyy HH:mm", Plain)).Append(" UTC from positions read from the fund's venues and wallets and from positions entered by hand. ");
        h.Append("Returns are time-weighted: money paid in or out is not counted as performance. Figures are unaudited.</div>");
        h.Append("</div></body></html>");
        return h.ToString();
    }

    /// <summary>The cumulative return of the fund and its benchmark as a small line chart, drawn as vector shapes so it prints sharply.</summary>
    private static string Chart(PerformanceDto p)
    {
        const double Width = 856,
            Height = 220,
            Left = 46,
            Bottom = 22,
            Top = 8;
        int n = p.Portfolio.Count;
        if (n < 2)
        {
            return "<p>Not enough days for a chart yet.</p>";
        }

        double low = Math.Min(0, Math.Min(p.Portfolio.Min(), p.BenchmarkSeries.Min())),
            high = Math.Max(0, Math.Max(p.Portfolio.Max(), p.BenchmarkSeries.Max()));
        if (high - low < 1e-9)
        {
            high = low + 0.01;
        }

        double X(int i) => Left + (Width - Left) * i / (n - 1);
        double Y(double v) => Top + (Height - Top - Bottom) * (1 - (v - low) / (high - low));
        string Line(IReadOnlyList<double> values) => string.Join(' ', values.Select((v, i) => $"{X(i).ToString("0.#", Plain)},{Y(v).ToString("0.#", Plain)}"));

        StringBuilder s = new();
        s.Append(Plain, $"<svg viewBox=\"0 0 {Width} {Height}\" width=\"100%\" role=\"img\" aria-label=\"Cumulative return of the fund and its benchmark\" xmlns=\"http://www.w3.org/2000/svg\">");
        for (int g = 0; g <= 4; g++)
        {
            double value = low + (high - low) * g / 4;
            string y = Y(value).ToString("0.#", Plain);
            s.Append(Plain, $"<line x1=\"{Left}\" x2=\"{Width}\" y1=\"{y}\" y2=\"{y}\" stroke=\"#ececec\"/><text x=\"{Left - 6}\" y=\"{y}\" dy=\"3\" font-size=\"10\" fill=\"#8c8c8c\" text-anchor=\"end\">{(value * 100).ToString("0", Plain)}%</text>");
        }

        s.Append(Plain, $"<polyline fill=\"none\" stroke=\"#9a9a9a\" stroke-width=\"1.4\" stroke-dasharray=\"4 3\" points=\"{Line(p.BenchmarkSeries)}\"/>");
        s.Append(Plain, $"<polyline fill=\"none\" stroke=\"#12356b\" stroke-width=\"2\" points=\"{Line(p.Portfolio)}\"/>");
        s.Append(Plain, $"<text x=\"{Left}\" y=\"{Height - 5}\" font-size=\"10\" fill=\"#8c8c8c\">{p.Dates[0]:d MMM yyyy}</text><text x=\"{Width}\" y=\"{Height - 5}\" font-size=\"10\" fill=\"#8c8c8c\" text-anchor=\"end\">{p.Dates[^1]:d MMM yyyy}</text>");
        s.Append(Plain, $"<text x=\"{Width / 2}\" y=\"{Height - 5}\" font-size=\"10\" fill=\"#12356b\" text-anchor=\"end\">— {E(p.Scope.Name)}</text><text x=\"{Width / 2 + 14}\" y=\"{Height - 5}\" font-size=\"10\" fill=\"#8c8c8c\">- - {E(p.Benchmark.Name)}</text>");
        return s.Append("</svg>").ToString();
    }

    // ---------------------------------------------------------------- daily NAV workbook

    /// <returns>Null when the fund does not exist or has never been valued.</returns>
    public async Task<ReportFile?> DailyNavAsync(int fundId, CancellationToken cancellationToken)
    {
        FundBook? book = await new FundBookReader(database).LoadAsync(fundId, cancellationToken);
        if (book is null)
        {
            return null;
        }

        int[] managerIds = book.Managers.Select(m => m.Id).ToArray();
        NavHistory history = await NavHistory.LoadAsync(database, managerIds, cancellationToken);
        int[] valued = history.Valued(managerIds);
        if (valued.Length == 0)
        {
            return null;
        }

        DateOnly asOf = history.AsOf(valued);
        NavSeries fund = history.Series(valued, asOf);
        double[] returns = fund.Returns();
        Dictionary<int, NavSeries> perManager = valued.ToDictionary(id => id, id => history.Series([id], asOf));
        Dictionary<int, Sleeve> sleeves = book.Sleeves.ToDictionary(s => s.Id);
        List<ManagerAccount> managers = book.Managers.Where(m => valued.Contains(m.Id)).OrderBy(m => sleeves[m.SleeveId].SortOrder).ThenBy(m => m.SleeveId).ThenBy(m => m.Id).ToList();

        using XLWorkbook workbook = new();
        IXLWorksheet nav = workbook.AddWorksheet("Daily NAV");
        string[] head = ["Date", "Fund NAV (USD)", "Net flow (USD)", "Day return", .. managers.Select(m => $"{m.Name} (USD)")];
        for (int c = 0; c < head.Length; c++)
        {
            nav.Cell(1, c + 1).Value = head[c];
        }

        int first = Math.Max(0, fund.Dates.Count - NavDays);
        int row = 2;
        for (int t = fund.Dates.Count - 1; t >= first; t--, row++)
        {
            DateOnly day = fund.Dates[t];
            nav.Cell(row, 1).Value = day.ToDateTime(TimeOnly.MinValue);
            nav.Cell(row, 2).Value = fund.Navs[t];
            nav.Cell(row, 3).Value = fund.Flows[t];
            if (t > 0)
            {
                nav.Cell(row, 4).Value = returns[t - 1];
            }

            for (int m = 0; m < managers.Count; m++)
            {
                NavSeries own = perManager[managers[m].Id];
                int at = own.Dates.IndexOf(day);
                if (at >= 0)
                {
                    nav.Cell(row, 5 + m).Value = own.Navs[at];
                }
            }
        }

        Style(nav, head.Length, row - 1, dateColumn: 1, percentColumns: [4], moneyFrom: 2);

        IXLWorksheet today = workbook.AddWorksheet("Managers");
        string[] managerHead = ["Sleeve", "Manager", "NAV (USD)", "Long (USD)", "Short (USD)", "Gross ÷ NAV", "Net ÷ NAV", "Allocated capital (USD)", "Status"];
        for (int c = 0; c < managerHead.Length; c++)
        {
            today.Cell(1, c + 1).Value = managerHead[c];
        }

        row = 2;
        foreach (ManagerAccount manager in managers)
        {
            BookTotals totals = HoldingsAggregator.Totals(book.Rows.Where(r => r.ManagerId == manager.Id).ToList());
            today.Cell(row, 1).Value = sleeves[manager.SleeveId].Name;
            today.Cell(row, 2).Value = manager.Name;
            today.Cell(row, 3).Value = totals.NavUsd;
            today.Cell(row, 4).Value = totals.LongUsd;
            today.Cell(row, 5).Value = totals.ShortUsd;
            if (totals.NavUsd > 0m)
            {
                today.Cell(row, 6).Value = totals.GrossUsd / totals.NavUsd;
                today.Cell(row, 7).Value = totals.NetUsd / totals.NavUsd;
            }

            today.Cell(row, 8).Value = manager.AllocatedCapitalUsd;
            today.Cell(row, 9).Value = manager.RetiredOn is DateOnly retired ? $"Retired {retired:yyyy-MM-dd}" : "Active";
            row++;
        }

        Style(today, managerHead.Length, row - 1, dateColumn: null, percentColumns: [6, 7], moneyFrom: 3);

        IXLWorksheet flows = workbook.AddWorksheet("Cash flows");
        string[] flowHead = ["Date", "Sleeve", "Manager", "Amount (USD)", "Kind", "Note"];
        for (int c = 0; c < flowHead.Length; c++)
        {
            flows.Cell(1, c + 1).Value = flowHead[c];
        }

        DateOnly since = asOf.AddDays(-NavDays);
        var recent = await database
            .CashFlows.AsNoTracking()
            .Where(f => managerIds.Contains(f.ManagerAccountId) && f.Date >= since)
            .OrderByDescending(f => f.Date)
            .ThenByDescending(f => f.Id)
            .Select(f => new { f.Date, f.ManagerAccountId, f.AmountUsd, f.IsBookChange, f.ExternalId, f.Note })
            .ToListAsync(cancellationToken);
        Dictionary<int, ManagerAccount> byId = book.Managers.ToDictionary(m => m.Id);
        row = 2;
        foreach (var flow in recent)
        {
            ManagerAccount manager = byId[flow.ManagerAccountId];
            flows.Cell(row, 1).Value = flow.Date.ToDateTime(TimeOnly.MinValue);
            flows.Cell(row, 2).Value = sleeves[manager.SleeveId].Name;
            flows.Cell(row, 3).Value = manager.Name;
            flows.Cell(row, 4).Value = flow.AmountUsd;
            flows.Cell(row, 5).Value = flow.IsBookChange ? "Account entering or leaving the book" : flow.ExternalId is null ? "Entered by hand" : flow.AmountUsd >= 0m ? "Deposit" : "Withdrawal";
            // A note that starts like a formula would be run by the spreadsheet; it is kept as text.
            flows.Cell(row, 6).SetValue(flow.Note ?? string.Empty);
            row++;
        }

        Style(flows, flowHead.Length, row - 1, dateColumn: 1, percentColumns: [], moneyFrom: 4, moneyTo: 4);

        using MemoryStream stream = new();
        workbook.SaveAs(stream);
        decimal navNow = fund.Navs[^1];
        return new ReportFile(
            $"{Slug(book.Fund.Name)}-daily-nav-{asOf:yyyy-MM-dd}.xlsx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            stream.ToArray(),
            $"{book.Fund.Name}: NAV {Usd(navNow)} on {asOf:d MMM yyyy}",
            $"NAV {Usd(navNow)} as of {asOf:d MMM yyyy} (UTC){(returns.Length > 0 ? $", {Pct(returns[^1])} on the day" : string.Empty)}. {managers.Count} managers, {NavDays} days of history in the attached workbook."
        );
    }

    private static void Style(IXLWorksheet sheet, int columns, int lastRow, int? dateColumn, int[] percentColumns, int moneyFrom, int? moneyTo = null)
    {
        IXLRange head = sheet.Range(1, 1, 1, columns);
        head.Style.Font.Bold = true;
        head.Style.Fill.BackgroundColor = XLColor.FromHtml("#F2F4F7");
        sheet.SheetView.FreezeRows(1);
        if (lastRow >= 2)
        {
            for (int c = moneyFrom; c <= (moneyTo ?? columns); c++)
            {
                sheet.Range(2, c, lastRow, c).Style.NumberFormat.Format = percentColumns.Contains(c) ? "0.00%" : "#,##0.00";
            }

            if (dateColumn is int d)
            {
                sheet.Range(2, d, lastRow, d).Style.DateFormat.Format = "yyyy-mm-dd";
            }
        }

        sheet.Columns(1, columns).AdjustToContents(1, Math.Min(lastRow, 60));
    }

    // ---------------------------------------------------------------- text

    private static void Tile(StringBuilder h, string label, string value, string below, double? tone = null) =>
        h.Append("<div class=\"tile\"><div class=\"k\">").Append(E(label)).Append("</div><div class=\"v").Append(tone is > 0 ? " up" : tone is < 0 ? " down" : string.Empty).Append("\">").Append(E(value)).Append("</div><div class=\"d\">").Append(below).Append("</div></div>");

    private static string Cell(double? value, int digits = 1) =>
        value is double v ? $"<td class=\"n {(v > 0 ? "up" : v < 0 ? "down" : string.Empty)}\">{Pct(v, digits)}</td>" : "<td class=\"n\">–</td>";

    private static string Money(decimal value, bool toned = false) => $"<td class=\"n{(toned ? value > 0m ? " up" : value < 0m ? " down" : string.Empty : string.Empty)}\">{Usd(value)}</td>";

    private static string Plain_(string text) => $"<td class=\"n\">{E(text)}</td>";

    private static string E(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

    public static string Usd(decimal value) => (value < 0m ? "−$" : "$") + Math.Abs(value).ToString("#,##0.00", Plain);

    public static string Pct(double value, int digits = 1)
    {
        string text = Math.Abs(value * 100).ToString("F" + digits, Plain) + "%";
        return value < 0 ? "−" + text : value > 0 ? "+" + text : text;
    }

    private static string Share(double? value) => value is double v ? (v * 100).ToString("0.0", Plain).Replace("-", "−", StringComparison.Ordinal) + "%" : "–";

    private static string Ratio(decimal part, decimal whole) => whole > 0m ? (part / whole * 100m).ToString("0", Plain).Replace("-", "−", StringComparison.Ordinal) + "%" : "–";

    /// <summary>A fund's name as part of a file name.</summary>
    private static string Slug(string name)
    {
        string slug = new(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        slug = string.Join('-', slug.Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? "fund" : slug.Length <= 60 ? slug : slug[..60];
    }
}
