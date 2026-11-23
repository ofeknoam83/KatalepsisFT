using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using KatalepsisFT.Application.Audit;
using KatalepsisFT.Domain.Access;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace KatalepsisFT.Web.Audit;

/// <summary>
/// Marks an endpoint whose successful calls go into the audit trail. One of the properties names the route value
/// (or, for <see cref="ManagerFromBody"/>, the request field) that says what the change is about.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AuditAttribute(string action) : Attribute
{
    public string Action => action;
    public string? Fund { get; init; }
    public string? Sleeve { get; init; }
    public string? Manager { get; init; }
    public string? Connection { get; init; }
    public string? Position { get; init; }
    public string? CashFlow { get; init; }
    public string? Rule { get; init; }
    public string? Alert { get; init; }
    public string? Schedule { get; init; }

    /// <summary>The request carries the manager's id in a field called ManagerId.</summary>
    public bool ManagerFromBody { get; init; }

    /// <summary>The fund does not exist until the call succeeds; its id is in the answer.</summary>
    public bool FundFromResult { get; init; }
}

/// <summary>
/// Writes the audit trail for every endpoint marked with <see cref="AuditAttribute"/>. What the change is about is
/// looked up before it happens, so removals can still be named; the entry is written only if the call succeeded.
/// </summary>
public sealed class AuditFilter(AuditLog audit, ILogger<AuditFilter> logger) : IAsyncActionFilter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>Never written down, whatever the request.</summary>
    private static readonly HashSet<string> Secret = new(StringComparer.OrdinalIgnoreCase) { "apiSecret", "passphrase", "password", "currentPassword", "newPassword" };

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        AuditAttribute? mark = context.ActionDescriptor.EndpointMetadata.OfType<AuditAttribute>().FirstOrDefault();
        if (mark is null)
        {
            await next();
            return;
        }

        CancellationToken cancellationToken = context.HttpContext.RequestAborted;
        AuditTarget target = AuditTarget.None;
        object? body = context.ActionArguments.Values.FirstOrDefault(v => v is not null && v is not CancellationToken && !v.GetType().IsPrimitive && v is not string);
        try
        {
            target = await TargetAsync(mark, context, body, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "The subject of an audited change could not be looked up.");
        }

        ActionExecutedContext done = await next();
        int status = done.Result is IStatusCodeActionResult { StatusCode: int code } ? code : done.Exception is null ? StatusCodes.Status200OK : StatusCodes.Status500InternalServerError;
        if (status is < 200 or >= 300)
        {
            return;
        }

        try
        {
            if (mark.FundFromResult && done.Result is ObjectResult { Value: { } created } && created.GetType().GetProperty("Id")?.GetValue(created) is int fundId)
            {
                target = await audit.FundAsync(fundId, cancellationToken);
            }

            await audit.RecordAsync(context.HttpContext.User.Identity?.Name ?? "Unknown", mark.Action, target, Describe(body), cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The change itself went through and has been answered for; losing its audit line must be loud, not fatal.
            logger.LogError(e, "An audit entry for \"{Action}\" could not be written.", mark.Action);
        }
    }

    private async Task<AuditTarget> TargetAsync(AuditAttribute mark, ActionExecutingContext context, object? body, CancellationToken cancellationToken)
    {
        int? Route(string? key) => key is not null && context.RouteData.Values.TryGetValue(key, out object? value) && int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out int id) ? id : null;
        if (Route(mark.Manager) is int managerId)
        {
            return await audit.ManagerAsync(managerId, cancellationToken);
        }

        if (mark.ManagerFromBody && body?.GetType().GetProperty("ManagerId")?.GetValue(body) is int fromBody)
        {
            return await audit.ManagerAsync(fromBody, cancellationToken);
        }

        if (Route(mark.Sleeve) is int sleeveId)
        {
            return await audit.SleeveAsync(sleeveId, cancellationToken);
        }

        if (Route(mark.Connection) is int connectionId)
        {
            return await audit.ConnectionAsync(connectionId, cancellationToken);
        }

        if (Route(mark.Position) is int positionId)
        {
            return await audit.PositionAsync(positionId, cancellationToken);
        }

        if (mark.CashFlow is not null && context.RouteData.Values.TryGetValue(mark.CashFlow, out object? flow) && long.TryParse(Convert.ToString(flow, CultureInfo.InvariantCulture), out long flowId))
        {
            return await audit.CashFlowAsync(flowId, cancellationToken);
        }

        if (Route(mark.Rule) is int ruleId)
        {
            return await audit.RuleAsync(ruleId, cancellationToken);
        }

        if (Route(mark.Alert) is int alertId)
        {
            return await audit.AlertAsync(alertId, cancellationToken);
        }

        if (Route(mark.Schedule) is int scheduleId)
        {
            return await audit.ScheduleAsync(scheduleId, cancellationToken);
        }

        return Route(mark.Fund) is int fundId ? await audit.FundAsync(fundId, cancellationToken) : AuditTarget.None;
    }

    /// <summary>What was sent, as short text: secrets dropped, keys reduced to their last four characters, files to their size.</summary>
    public static string? Describe(object? body)
    {
        if (body is null || JsonSerializer.SerializeToNode(body, Json) is not JsonObject sent)
        {
            return null;
        }

        StringBuilder text = new();
        foreach ((string name, JsonNode? value) in sent)
        {
            if (value is null || Secret.Contains(name))
            {
                continue;
            }

            string shown = value switch
            {
                JsonValue v when name.Equals("csv", StringComparison.OrdinalIgnoreCase) => $"file of {v.ToString().Split('\n').Length} lines",
                JsonValue v when name.Equals("apiKey", StringComparison.OrdinalIgnoreCase) => "…" + (v.ToString().Length <= 4 ? v.ToString() : v.ToString()[^4..]),
                JsonValue v => v.ToString(),
                _ => value.ToJsonString(Json),
            };
            if (shown.Length > 300)
            {
                shown = shown[..300] + "…";
            }

            text.Append(text.Length == 0 ? string.Empty : "; ").Append(name).Append(": ").Append(shown);
        }

        return text.Length == 0 ? null : text.ToString();
    }
}

/// <summary>Reading the audit trail. Admin only.</summary>
[ApiController]
[Route("api/audit")]
[Authorize(Roles = Roles.Admin)]
public sealed class AuditController(AuditLog audit, TimeProvider clock) : ControllerBase
{
    /// <param name="fundId">Only this fund's entries; left out, everything including sign-ins.</param>
    /// <param name="from">First day, UTC; thirty days back when left out.</param>
    /// <param name="format">"csv" for a file.</param>
    [HttpGet]
    public async Task<IActionResult> ListAsync(int? fundId = null, DateOnly? from = null, DateOnly? to = null, string? format = null, CancellationToken cancellationToken = default)
    {
        DateOnly last = to ?? DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        DateOnly first = from ?? last.AddDays(-30);
        if (first > last || last.DayNumber - first.DayNumber > 3660)
        {
            return BadRequest(new KatalepsisFT.Application.Auth.ErrorResponse("Choose a first day on or before the last, at most ten years apart."));
        }

        AuditListDto list = await audit.ListAsync(fundId, first, last, cancellationToken);
        if (!string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
        {
            return Ok(list);
        }

        StringBuilder csv = new("when_utc,user,action,fund,subject,detail\n");
        foreach (AuditEntryDto e in list.Entries)
        {
            csv.Append(e.At.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',').Append(Csv.Cell(e.UserName)).Append(',').Append(Csv.Cell(e.Action)).Append(',').Append(Csv.Cell(e.FundName)).Append(',').Append(Csv.Cell(e.Subject)).Append(',').Append(Csv.Cell(e.Detail)).Append('\n');
        }

        return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", $"audit-trail-{first:yyyy-MM-dd}-to-{last:yyyy-MM-dd}.csv");
    }
}

/// <summary>Writing cells of a CSV file.</summary>
public static class Csv
{
    /// <summary>Quoted when needed. A cell that a spreadsheet would run as a formula is made plain text.</summary>
    public static string Cell(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (text[0] is '=' or '+' or '-' or '@')
        {
            text = "'" + text;
        }

        return text.IndexOfAny([',', '"', '\n', '\r']) < 0 ? text : "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
