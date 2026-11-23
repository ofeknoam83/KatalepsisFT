using KatalepsisFT.Web.Audit;
using System.Security.Claims;
using FluentValidation;
using FluentValidation.Results;
using KatalepsisFT.Application.Alerts;
using KatalepsisFT.Application.Auth;
using KatalepsisFT.Domain.Access;
using KatalepsisFT.Persistence.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KatalepsisFT.Web.Alerts;

public sealed record AssigneeDto(int Id, string Name);

/// <summary>The alert inbox and rules of a fund. Everyone signed in can read; only an Admin can act on alerts or change rules.</summary>
[ApiController]
[Route("api")]
public sealed class AlertsController(
    AlertService alerts,
    AlertEvaluator evaluator,
    UserManager<AppUser> users,
    IValidator<SaveAlertRuleRequest> ruleValidator,
    IValidator<SnoozeRequest> snoozeValidator,
    IValidator<NoteRequest> noteValidator
) : ControllerBase
{
    [HttpGet("funds/{fundId:int}/alerts")]
    public async Task<ActionResult<AlertListDto>> ListAsync(int fundId, AlertListState state = AlertListState.Active, int? sleeveId = null, CancellationToken cancellationToken = default)
    {
        AlertListDto? result = await alerts.ListAsync(fundId, state, sleeveId, cancellationToken);
        return result is null ? NotFound() : result;
    }

    /// <summary>Compares every rule with the latest figures now, instead of waiting for the next scheduled run.</summary>
    [Authorize(Roles = Roles.Admin)]
    [HttpPost("funds/{fundId:int}/alerts/evaluate")]
    public async Task<ActionResult<EvaluationResultDto>> EvaluateAsync(int fundId, CancellationToken cancellationToken)
    {
        if (await alerts.ListRulesAsync(fundId, cancellationToken) is null)
        {
            return NotFound();
        }

        return await evaluator.EvaluateAsync(fundId, cancellationToken);
    }

    [Authorize(Roles = Roles.Admin)]
    [HttpGet("alerts/assignees")]
    public async Task<List<AssigneeDto>> AssigneesAsync(CancellationToken cancellationToken) =>
        (await users.Users.AsNoTracking().OrderBy(u => u.DisplayName).ThenBy(u => u.Id).Select(u => new { u.Id, u.DisplayName, u.Email }).ToListAsync(cancellationToken))
            .Select(u => new AssigneeDto(u.Id, string.IsNullOrWhiteSpace(u.DisplayName) ? u.Email ?? $"User {u.Id}" : u.DisplayName))
            .ToList();

    [Authorize(Roles = Roles.Admin)]
    [Audit("Alert acknowledged", Alert = "id")]
    [HttpPost("alerts/{id:int}/acknowledge")]
    public async Task<IActionResult> AcknowledgeAsync(int id, CancellationToken cancellationToken) => Result(await alerts.AcknowledgeAsync(id, await ActorAsync(), cancellationToken));

    [Authorize(Roles = Roles.Admin)]
    [Audit("Alert assigned", Alert = "id")]
    [HttpPost("alerts/{id:int}/assign")]
    public async Task<IActionResult> AssignAsync(int id, AssignRequest request, CancellationToken cancellationToken)
    {
        Actor? assignee = null;
        if (request.UserId is int userId)
        {
            AppUser? user = await users.FindByIdAsync(userId.ToString(global::System.Globalization.CultureInfo.InvariantCulture));
            if (user is null)
            {
                return BadRequest(new ErrorResponse("That person does not exist."));
            }

            assignee = Describe(user);
        }

        return Result(await alerts.AssignAsync(id, await ActorAsync(), assignee, cancellationToken));
    }

    [Authorize(Roles = Roles.Admin)]
    [Audit("Alert commented on", Alert = "id")]
    [HttpPost("alerts/{id:int}/comment")]
    public async Task<IActionResult> CommentAsync(int id, NoteRequest request, CancellationToken cancellationToken)
    {
        if (await InvalidAsync(noteValidator, request, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        if (string.IsNullOrWhiteSpace(request.Note))
        {
            return BadRequest(new ErrorResponse("Write a comment first."));
        }

        return Result(await alerts.CommentAsync(id, await ActorAsync(), request.Note, cancellationToken));
    }

    [Authorize(Roles = Roles.Admin)]
    [Audit("Alert snoozed", Alert = "id")]
    [HttpPost("alerts/{id:int}/snooze")]
    public async Task<IActionResult> SnoozeAsync(int id, SnoozeRequest request, CancellationToken cancellationToken) =>
        await InvalidAsync(snoozeValidator, request, cancellationToken) ?? Result(await alerts.SnoozeAsync(id, await ActorAsync(), request.Days, request.Note, cancellationToken));

    [Authorize(Roles = Roles.Admin)]
    [Audit("Alert resolved by hand", Alert = "id")]
    [HttpPost("alerts/{id:int}/resolve")]
    public async Task<IActionResult> ResolveAsync(int id, NoteRequest request, CancellationToken cancellationToken) =>
        await InvalidAsync(noteValidator, request, cancellationToken) ?? Result(await alerts.ResolveAsync(id, await ActorAsync(), request.Note, cancellationToken));

    /// <summary>The breach register: every breach active at any time between the two days. "format=csv" gives a file.</summary>
    [HttpGet("funds/{fundId:int}/breaches")]
    public async Task<IActionResult> BreachesAsync(int fundId, DateOnly? from = null, DateOnly? to = null, string? format = null, CancellationToken cancellationToken = default)
    {
        DateOnly last = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
        DateOnly first = from ?? last.AddDays(-90);
        if (first > last || last.DayNumber - first.DayNumber > 3660)
        {
            return BadRequest(new KatalepsisFT.Application.Auth.ErrorResponse("Choose a first day on or before the last, at most ten years apart."));
        }

        BreachRegisterDto? register = await alerts.BreachRegisterAsync(fundId, first, last, cancellationToken);
        if (register is null)
        {
            return NotFound();
        }

        if (!string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
        {
            return Ok(register);
        }

        global::System.Globalization.CultureInfo plain = global::System.Globalization.CultureInfo.InvariantCulture;
        global::System.Text.StringBuilder csv = new("id,severity,mandate_limit,scope,scope_name,rule,subject,raised_utc,ended_utc,hours,limit,peak,last,status,acknowledged_by,acknowledged_utc,outcome,note\n");
        foreach (BreachDto b in register.Rows)
        {
            csv.AppendJoin(
                    ',',
                    b.Id.ToString(plain),
                    b.Severity,
                    b.FromMandate ? "yes" : "no",
                    b.ScopeKind,
                    Csv.Cell(b.ScopeName),
                    Csv.Cell(b.Rule),
                    Csv.Cell(b.Subject),
                    b.RaisedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", plain),
                    b.EndedAt?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", plain),
                    b.DurationHours.ToString("0.##", plain),
                    AlertText.Figure(b.Metric, b.Threshold),
                    AlertText.Figure(b.Metric, b.PeakValue),
                    AlertText.Figure(b.Metric, b.LastValue),
                    b.Status,
                    Csv.Cell(b.AcknowledgedBy),
                    b.AcknowledgedAt?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", plain),
                    Csv.Cell(b.Outcome),
                    Csv.Cell(b.Note)
                )
                .Append('\n');
        }

        return File(global::System.Text.Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", $"breach-register-{first:yyyy-MM-dd}-to-{last:yyyy-MM-dd}.csv");
    }

    [HttpGet("funds/{fundId:int}/alert-rules")]
    public async Task<ActionResult<AlertRulesDto>> RulesAsync(int fundId, CancellationToken cancellationToken)
    {
        AlertRulesDto? result = await alerts.ListRulesAsync(fundId, cancellationToken);
        return result is null ? NotFound() : result;
    }

    [Authorize(Roles = Roles.Admin)]
    [Audit("Alert rule added", Fund = "fundId")]
    [HttpPost("funds/{fundId:int}/alert-rules")]
    public async Task<IActionResult> CreateRuleAsync(int fundId, SaveAlertRuleRequest request, CancellationToken cancellationToken) =>
        await InvalidAsync(ruleValidator, request, cancellationToken) ?? Result(await alerts.CreateRuleAsync(fundId, request, cancellationToken));

    [Authorize(Roles = Roles.Admin)]
    [Audit("Alert rule changed", Rule = "id")]
    [HttpPut("alert-rules/{id:int}")]
    public async Task<IActionResult> UpdateRuleAsync(int id, SaveAlertRuleRequest request, CancellationToken cancellationToken) =>
        await InvalidAsync(ruleValidator, request, cancellationToken) ?? Result(await alerts.UpdateRuleAsync(id, request, cancellationToken));

    [Authorize(Roles = Roles.Admin)]
    [Audit("Alert rule removed", Rule = "id")]
    [HttpDelete("alert-rules/{id:int}")]
    public async Task<IActionResult> DeleteRuleAsync(int id, CancellationToken cancellationToken)
    {
        AlertChange<AlertRuleDto> change = await alerts.DeleteRuleAsync(id, cancellationToken);
        return change.Value is not null ? NoContent() : Result(change);
    }

    private IActionResult Result<T>(AlertChange<T> change)
        where T : class
    {
        if (change.Value is not null)
        {
            return Ok(change.Value);
        }

        return change.Error is null ? NotFound() : Conflict(new ErrorResponse(change.Error));
    }

    private async Task<IActionResult?> InvalidAsync<T>(IValidator<T> validator, T request, CancellationToken cancellationToken)
    {
        ValidationResult validation = await validator.ValidateAsync(request, cancellationToken);
        return validation.IsValid ? null : BadRequest(new ErrorResponse(validation.Errors[0].ErrorMessage));
    }

    private async Task<Actor> ActorAsync()
    {
        AppUser? user = await users.GetUserAsync(User);
        return user is null ? new Actor(int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!, global::System.Globalization.CultureInfo.InvariantCulture), User.Identity?.Name ?? "Unknown") : Describe(user);
    }

    private static Actor Describe(AppUser user) => new(user.Id, string.IsNullOrWhiteSpace(user.DisplayName) ? user.Email ?? $"User {user.Id}" : user.DisplayName);
}
