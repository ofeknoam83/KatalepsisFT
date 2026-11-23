using KatalepsisFT.Application.Auth;
using KatalepsisFT.Application.Structure;
using KatalepsisFT.Domain.Access;
using KatalepsisFT.Web.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KatalepsisFT.Web.Connections;

/// <summary>Renaming things. Each change is one name and goes into the audit trail with the name it replaced.</summary>
[ApiController]
[Route("api")]
[Authorize(Roles = Roles.Admin)]
public sealed class NamesController(RenameService names) : ControllerBase
{
    [Audit("Fund renamed", Fund = "id")]
    [HttpPost("funds/{id:int}/name")]
    public async Task<IActionResult> FundAsync(int id, RenameRequest request, CancellationToken cancellationToken) => Result(await names.FundAsync(id, request.Name, cancellationToken));

    [Audit("Account owner's name changed", Fund = "id")]
    [HttpPost("funds/{id:int}/owner")]
    public async Task<IActionResult> FundOwnerAsync(int id, RenameRequest request, CancellationToken cancellationToken) => Result(await names.FundOwnerAsync(id, request.Name, cancellationToken));

    [Audit("Sleeve renamed", Sleeve = "id")]
    [HttpPost("sleeves/{id:int}/name")]
    public async Task<IActionResult> SleeveAsync(int id, RenameRequest request, CancellationToken cancellationToken) => Result(await names.SleeveAsync(id, request.Name, cancellationToken));

    [Audit("Manager renamed", Manager = "id")]
    [HttpPost("managers/{id:int}/name")]
    public async Task<IActionResult> ManagerAsync(int id, RenameRequest request, CancellationToken cancellationToken) => Result(await names.ManagerAsync(id, request.Name, cancellationToken));

    [Audit("Key or wallet renamed", Connection = "id")]
    [HttpPost("connections/{id:int}/label")]
    public async Task<IActionResult> ConnectionAsync(int id, RenameRequest request, CancellationToken cancellationToken) => Result(await names.ConnectionAsync(id, request.Name, cancellationToken));

    [Audit("Trading account renamed", TradingAccount = "id")]
    [HttpPost("trading-accounts/{id:int}/label")]
    public async Task<IActionResult> TradingAccountAsync(int id, RenameRequest request, CancellationToken cancellationToken) => Result(await names.TradingAccountAsync(id, request.Name, cancellationToken));

    [Audit("Strategy key renamed")]
    [HttpPost("strategy-keys/{id:int}/name")]
    public async Task<IActionResult> StrategyKeyAsync(int id, RenameRequest request, CancellationToken cancellationToken) => Result(await names.StrategyKeyAsync(id, request.Name, cancellationToken));

    private IActionResult Result(StructureChange change) => change.Done ? NoContent() : change.Error is null ? NotFound() : Conflict(new ErrorResponse(change.Error));
}
