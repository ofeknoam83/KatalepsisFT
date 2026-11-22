using FluentValidation;
using FluentValidation.Results;
using KatalepsisFT.Application.Auth;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Application.Structure;
using KatalepsisFT.Domain.Access;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KatalepsisFT.Web.Connections;

public sealed record SetEnabledRequest(bool Enabled);

/// <summary>Setting up real funds and the venue keys their managers are read through. Admin only, all of it.</summary>
[ApiController]
[Route("api")]
[Authorize(Roles = Roles.Admin)]
public sealed class ConnectionsController(ConnectionService connections, StructureService structure, IValidator<AddConnectionRequest> connectionValidator, IValidator<NewFundRequest> fundValidator) : ControllerBase
{
    [HttpPost("funds")]
    public async Task<IActionResult> CreateFundAsync(NewFundRequest request, CancellationToken cancellationToken)
    {
        ValidationResult validation = await fundValidator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return BadRequest(new ErrorResponse(validation.Errors[0].ErrorMessage));
        }

        (FundCreatedDto? fund, string? error) = await structure.CreateFundAsync(request, cancellationToken);
        return fund is null ? Conflict(new ErrorResponse(error!)) : Ok(fund);
    }

    [HttpGet("funds/{fundId:int}/connections")]
    public async Task<ActionResult<ConnectionsDto>> ListAsync(int fundId, CancellationToken cancellationToken)
    {
        ConnectionsDto? result = await connections.ListAsync(fundId, cancellationToken);
        return result is null ? NotFound() : result;
    }

    /// <summary>The key is checked with the venue first; one that can trade or withdraw is refused and nothing is stored.</summary>
    [HttpPost("connections")]
    public async Task<IActionResult> AddAsync(AddConnectionRequest request, CancellationToken cancellationToken)
    {
        ValidationResult validation = await connectionValidator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return BadRequest(new ErrorResponse(validation.Errors[0].ErrorMessage));
        }

        return Result(await connections.AddAsync(request, User.Identity?.Name ?? "Unknown", cancellationToken));
    }

    [HttpPost("connections/{id:int}/sync")]
    public async Task<IActionResult> SyncAsync(int id, CancellationToken cancellationToken) => Result(await connections.SyncNowAsync(id, cancellationToken));

    [HttpPost("connections/{id:int}/enabled")]
    public async Task<IActionResult> SetEnabledAsync(int id, SetEnabledRequest request, CancellationToken cancellationToken) => Result(await connections.SetEnabledAsync(id, request.Enabled, cancellationToken));

    [HttpDelete("connections/{id:int}")]
    public async Task<IActionResult> DeleteAsync(int id, CancellationToken cancellationToken) => await connections.DeleteAsync(id, cancellationToken) ? NoContent() : NotFound();

    private IActionResult Result(ConnectionChange change)
    {
        if (change.Value is not null)
        {
            return Ok(change.Value);
        }

        return change.Error is null ? NotFound() : Conflict(new ErrorResponse(change.Error));
    }
}
