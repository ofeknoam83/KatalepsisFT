using KatalepsisFT.Web.Audit;
using KatalepsisFT.Application.Auth;
using KatalepsisFT.Application.History;
using KatalepsisFT.Application.Manual;
using KatalepsisFT.Domain.Access;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KatalepsisFT.Web.Manual;

/// <param name="Csv">The file's text.</param>
public sealed record UploadRequest(string Csv);

public sealed record ManualErrorsResponse(string Message, IReadOnlyList<string> Errors);

/// <summary>Hand-entered positions and cash flows of a manager. Admin only.</summary>
[ApiController]
[Route("api")]
[Authorize(Roles = Roles.Admin)]
public sealed class ManualController(ManualPositionService manual, TrackRecordService trackRecords) : ControllerBase
{
    /// <summary>A file larger than this is not a book of positions.</summary>
    private const int MaxUploadCharacters = 1_000_000;

    [HttpGet("managers/{managerId:int}/manual")]
    public async Task<ActionResult<ManualBookDto>> GetAsync(int managerId, CancellationToken cancellationToken)
    {
        ManualBookDto? result = await manual.GetAsync(managerId, cancellationToken);
        return result is null ? NotFound() : result;
    }

    [Audit("Position entered by hand", Manager = "managerId")]
    [HttpPost("managers/{managerId:int}/manual")]
    public async Task<IActionResult> AddAsync(int managerId, ManualPositionRequest request, CancellationToken cancellationToken) => Result(await manual.AddAsync(managerId, request, UserName, cancellationToken));

    /// <summary>Replaces the manager's whole manual book with the file's lines, or changes nothing and says which lines are wrong.</summary>
    [Audit("Positions file uploaded", Manager = "managerId")]
    [HttpPost("managers/{managerId:int}/manual/upload")]
    public async Task<IActionResult> UploadAsync(int managerId, UploadRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Csv) || request.Csv.Length > MaxUploadCharacters)
        {
            return BadRequest(new ErrorResponse("Choose a CSV file of positions, up to 1 MB."));
        }

        return Result(await manual.UploadAsync(managerId, request.Csv, UserName, cancellationToken));
    }

    [Audit("Hand-entered position changed", Position = "id")]
    [HttpPut("manual-positions/{id:int}")]
    public async Task<IActionResult> UpdateAsync(int id, ManualPositionRequest request, CancellationToken cancellationToken) => Result(await manual.UpdateAsync(id, request, UserName, cancellationToken));

    [Audit("Hand-entered position removed", Position = "id")]
    [HttpDelete("manual-positions/{id:int}")]
    public async Task<IActionResult> DeleteAsync(int id, CancellationToken cancellationToken) => Result(await manual.DeleteAsync(id, cancellationToken));

    [Audit("Cash flow entered", Manager = "managerId")]
    [HttpPost("managers/{managerId:int}/cash-flows")]
    public async Task<IActionResult> AddCashFlowAsync(int managerId, CashFlowRequest request, CancellationToken cancellationToken) => Result(await manual.AddCashFlowAsync(managerId, request, cancellationToken));

    [Audit("Cash flow removed", CashFlow = "id")]
    [HttpDelete("cash-flows/{id:long}")]
    public async Task<IActionResult> DeleteCashFlowAsync(long id, CancellationToken cancellationToken) => Result(await manual.DeleteCashFlowAsync(id, cancellationToken));

    [HttpGet("managers/{managerId:int}/track-record")]
    public async Task<ActionResult<TrackRecordDto>> TrackRecordAsync(int managerId, CancellationToken cancellationToken)
    {
        TrackRecordDto? result = await trackRecords.GetAsync(managerId, cancellationToken);
        return result is null ? NotFound() : result;
    }

    /// <summary>Replaces the manager's imported history with the file's days, or changes nothing and says which lines are wrong.</summary>
    [Audit("Track record imported", Manager = "managerId")]
    [HttpPost("managers/{managerId:int}/track-record")]
    public async Task<IActionResult> ImportTrackRecordAsync(int managerId, UploadRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Csv) || request.Csv.Length > MaxUploadCharacters)
        {
            return BadRequest(new ErrorResponse("Choose a CSV file of daily NAV, up to 1 MB."));
        }

        return Result(await trackRecords.ImportAsync(managerId, request.Csv, cancellationToken));
    }

    [Audit("Imported track record removed", Manager = "managerId")]
    [HttpDelete("managers/{managerId:int}/track-record")]
    public async Task<IActionResult> RemoveTrackRecordAsync(int managerId, CancellationToken cancellationToken) => Result(await trackRecords.RemoveAsync(managerId, cancellationToken));

    private IActionResult Result(TrackRecordChange change) =>
        change.Done ? NoContent() : change.NotFound ? NotFound() : UnprocessableEntity(new ManualErrorsResponse(change.Errors[0], change.Errors));

    private string UserName => User.Identity?.Name ?? "Unknown";

    private IActionResult Result(ManualChange change)
    {
        if (change.Done)
        {
            return NoContent();
        }

        return change.NotFound ? NotFound() : UnprocessableEntity(new ManualErrorsResponse(change.Errors[0], change.Errors));
    }
}
