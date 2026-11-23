using FluentValidation;
using FluentValidation.Results;
using KatalepsisFT.Application.Auth;
using KatalepsisFT.Application.Reports;
using KatalepsisFT.Domain.Access;
using KatalepsisFT.Web.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KatalepsisFT.Web.Reports;

/// <summary>A fund's reports: read or downloaded by anyone signed in, scheduled for email by administrators.</summary>
[ApiController]
[Route("api")]
public sealed class ReportsController(ReportService reports, ReportScheduleService schedules, IValidator<SaveReportScheduleRequest> validator) : ControllerBase
{
    /// <summary>The factsheet as a page of its own: it opens in a browser tab and prints to a PDF from there.</summary>
    [HttpGet("funds/{fundId:int}/reports/factsheet")]
    public async Task<IActionResult> FactsheetAsync(int fundId, bool download = false, CancellationToken cancellationToken = default)
    {
        ReportFile? report = await reports.FactsheetAsync(fundId, cancellationToken);
        if (report is null)
        {
            return NotFound();
        }

        // The page carries its own styles and one line of script for its print button, and loads nothing from anywhere.
        Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; frame-ancestors 'none'";
        return download ? File(report.Content, report.ContentType, report.FileName) : File(report.Content, report.ContentType);
    }

    [HttpGet("funds/{fundId:int}/reports/daily-nav")]
    public async Task<IActionResult> DailyNavAsync(int fundId, CancellationToken cancellationToken)
    {
        ReportFile? report = await reports.DailyNavAsync(fundId, cancellationToken);
        return report is null ? NotFound() : File(report.Content, report.ContentType, report.FileName);
    }

    [Authorize(Roles = Roles.Admin)]
    [HttpGet("funds/{fundId:int}/report-schedules")]
    public async Task<ActionResult<ReportSchedulesDto>> SchedulesAsync(int fundId, CancellationToken cancellationToken)
    {
        ReportSchedulesDto? result = await schedules.ListAsync(fundId, cancellationToken);
        return result is null ? NotFound() : result;
    }

    [Authorize(Roles = Roles.Admin)]
    [Audit("Report schedule added", Fund = "fundId")]
    [HttpPost("funds/{fundId:int}/report-schedules")]
    public async Task<IActionResult> AddAsync(int fundId, SaveReportScheduleRequest request, CancellationToken cancellationToken) =>
        await InvalidAsync(request, cancellationToken) ?? Result(await schedules.AddAsync(fundId, request, User.Identity?.Name ?? "Unknown", cancellationToken));

    [Authorize(Roles = Roles.Admin)]
    [Audit("Report schedule changed", Schedule = "id")]
    [HttpPut("report-schedules/{id:int}")]
    public async Task<IActionResult> SaveAsync(int id, SaveReportScheduleRequest request, CancellationToken cancellationToken) =>
        await InvalidAsync(request, cancellationToken) ?? Result(await schedules.SaveAsync(id, request, cancellationToken));

    [Authorize(Roles = Roles.Admin)]
    [Audit("Report schedule removed", Schedule = "id")]
    [HttpDelete("report-schedules/{id:int}")]
    public async Task<IActionResult> DeleteAsync(int id, CancellationToken cancellationToken) => Result(await schedules.DeleteAsync(id, cancellationToken));

    [Authorize(Roles = Roles.Admin)]
    [Audit("Report sent by hand", Schedule = "id")]
    [HttpPost("report-schedules/{id:int}/send")]
    public async Task<IActionResult> SendAsync(int id, CancellationToken cancellationToken) => Result(await schedules.SendNowAsync(id, cancellationToken));

    private async Task<IActionResult?> InvalidAsync(SaveReportScheduleRequest request, CancellationToken cancellationToken)
    {
        ValidationResult validation = await validator.ValidateAsync(request, cancellationToken);
        return validation.IsValid ? null : BadRequest(new ErrorResponse(validation.Errors[0].ErrorMessage));
    }

    private IActionResult Result(ScheduleChange change) => change.Done ? NoContent() : change.NotFound ? NotFound() : Conflict(new ErrorResponse(change.Error!));
}
