using KatalepsisFT.Application.Auth;
using KatalepsisFT.Application.Execution;
using KatalepsisFT.Domain.Access;
using KatalepsisFT.Domain.Execution;
using KatalepsisFT.Domain.Instruments;
using KatalepsisFT.Web.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace KatalepsisFT.Web.Execution;

public sealed record SetEnabledBody(bool Enabled);

/// <summary>
/// The execution side as people use it: trading accounts, the order ticket, the blotter, kill switches and
/// approvals. Anyone signed in can look; sending orders takes the Trader or Admin role; keys, limits and
/// approvals take Admin.
/// </summary>
[ApiController]
[Route("api")]
public sealed class TradingController(TradingService trading) : ControllerBase
{
    private Actor Actor => new(User.Identity?.Name ?? "Unknown", User.IsInRole(Roles.Admin) || User.IsInRole(Roles.Trader), User.IsInRole(Roles.Admin));

    [HttpGet("funds/{fundId:int}/trading")]
    public async Task<ActionResult<TradingDto>> GetAsync(int fundId, CancellationToken cancellationToken)
    {
        TradingDto? result = await trading.GetAsync(fundId, Actor, cancellationToken);
        return result is null ? NotFound() : result;
    }

    [HttpGet("funds/{fundId:int}/orders")]
    public async Task<ActionResult<IReadOnlyList<OrderDto>>> OrdersAsync(int fundId, bool open = false, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<OrderDto>? result = await trading.OrdersAsync(fundId, open, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet("orders/{id:long}")]
    public async Task<ActionResult<OrderDto>> OrderAsync(long id, CancellationToken cancellationToken)
    {
        OrderDto? result = await trading.OrderAsync(id, cancellationToken);
        return result is null ? NotFound() : result;
    }

    /// <summary>The consolidated book of one instrument: every venue's best prices and depth, side by side.</summary>
    [HttpGet("book/{type}/{asset}")]
    public async Task<BookDto> BookAsync(InstrumentType type, string asset, CancellationToken cancellationToken) => await trading.BookAsync(asset, type, cancellationToken);

    [Authorize(Roles = Roles.Admin)]
    [Audit("Trading account added", ManagerFromBody = true)]
    [HttpPost("trading-accounts")]
    public async Task<IActionResult> AddAccountAsync(SaveTradingAccountRequest request, CancellationToken cancellationToken) => Result(await trading.AddAccountAsync(request, Actor, cancellationToken));

    [Authorize(Roles = Roles.Admin)]
    [Audit("Trading limits changed", TradingAccount = "id")]
    [HttpPut("trading-accounts/{id:int}")]
    public async Task<IActionResult> SaveLimitsAsync(int id, SaveTradingAccountRequest request, CancellationToken cancellationToken) => Result(await trading.SaveLimitsAsync(id, request, Actor, cancellationToken));

    [Authorize(Roles = Roles.Admin)]
    [Audit("Trading key submitted for approval", TradingAccount = "id")]
    [HttpPost("trading-accounts/{id:int}/key")]
    public async Task<IActionResult> RequestKeyAsync(int id, TradingKeyRequest request, CancellationToken cancellationToken) => Result(await trading.RequestKeyAsync(id, request, Actor, cancellationToken));

    [Authorize(Roles = Roles.Admin)]
    [Audit("Real trading asked for", TradingAccount = "id")]
    [HttpPost("trading-accounts/{id:int}/go-live")]
    public async Task<IActionResult> GoLiveAsync(int id, CancellationToken cancellationToken) => Result(await trading.RequestGoLiveAsync(id, Actor, cancellationToken));

    [Authorize(Roles = Roles.Admin)]
    [Audit("Account taken back to paper", TradingAccount = "id")]
    [HttpPost("trading-accounts/{id:int}/paper")]
    public async Task<IActionResult> BackToPaperAsync(int id, CancellationToken cancellationToken) => Result(await trading.BackToPaperAsync(id, cancellationToken));

    [Authorize(Roles = Roles.CanTrade)]
    [Audit("Kill switch pulled", Fund = "fundId")]
    [HttpPost("funds/{fundId:int}/halts")]
    public async Task<IActionResult> PullHaltAsync(int fundId, PullHaltRequest request, CancellationToken cancellationToken) => Result(await trading.PullHaltAsync(fundId, request, Actor, cancellationToken));

    [Authorize(Roles = Roles.CanTrade)]
    [Audit("Kill switch release asked for")]
    [HttpPost("halts/{id:int}/release")]
    public async Task<IActionResult> RequestReleaseAsync(int id, CancellationToken cancellationToken) => Result(await trading.RequestReleaseAsync(id, Actor, cancellationToken));

    [Authorize(Roles = Roles.Admin)]
    [Audit("Request approved", Approval = "id")]
    [HttpPost("approvals/{id:int}/approve")]
    public async Task<IActionResult> ApproveAsync(int id, DecisionRequest request, CancellationToken cancellationToken) => Result(await trading.DecideAsync(id, true, request.Note, Actor, cancellationToken));

    [Authorize(Roles = Roles.Admin)]
    [Audit("Request rejected", Approval = "id")]
    [HttpPost("approvals/{id:int}/reject")]
    public async Task<IActionResult> RejectAsync(int id, DecisionRequest request, CancellationToken cancellationToken) => Result(await trading.DecideAsync(id, false, request.Note, Actor, cancellationToken));

    [Authorize(Roles = Roles.CanTrade)]
    [Audit("Request withdrawn", Approval = "id")]
    [HttpPost("approvals/{id:int}/cancel")]
    public async Task<IActionResult> CancelRequestAsync(int id, CancellationToken cancellationToken) => Result(await trading.CancelRequestAsync(id, Actor, cancellationToken));

    [Authorize(Roles = Roles.CanTrade)]
    [Audit("Order sent from the ticket", TradingAccountFromBody = true)]
    [HttpPost("orders")]
    public async Task<IActionResult> SubmitAsync(SubmitOrderRequest request, CancellationToken cancellationToken) => Result(await trading.SubmitAsync(request, Actor, null, cancellationToken));

    [Authorize(Roles = Roles.CanTrade)]
    [Audit("Order cancel asked for", Order = "id")]
    [HttpPost("orders/{id:long}/cancel")]
    public async Task<IActionResult> CancelAsync(long id, CancellationToken cancellationToken) => Result(await trading.CancelAsync(id, Actor, null, cancellationToken));

    [Authorize(Roles = Roles.Admin)]
    [HttpGet("strategy-keys")]
    public async Task<IReadOnlyList<StrategyKeyDto>> StrategyKeysAsync(CancellationToken cancellationToken) => await trading.StrategyKeysAsync(cancellationToken);

    [Authorize(Roles = Roles.Admin)]
    [Audit("Strategy key created")]
    [HttpPost("strategy-keys")]
    public async Task<IActionResult> AddStrategyKeyAsync(SaveStrategyKeyRequest request, CancellationToken cancellationToken) => Result(await trading.AddStrategyKeyAsync(request, Actor, cancellationToken));

    [Authorize(Roles = Roles.Admin)]
    [Audit("Strategy key switched on or off")]
    [HttpPost("strategy-keys/{id:int}/enabled")]
    public async Task<IActionResult> SetStrategyKeyEnabledAsync(int id, SetEnabledBody request, CancellationToken cancellationToken) => Result(await trading.SetStrategyKeyEnabledAsync(id, request.Enabled, cancellationToken));

    private IActionResult Result<T>(TradingChange<T> change) =>
        change.Done ? Ok(new { value = change.Value, notice = change.Notice }) : change.NotFound ? NotFound() : Conflict(new ErrorResponse(change.Error!));
}

/// <summary>
/// The same order path for strategies. A strategy does not sign in; it sends its key in the X-KFT-Strategy-Key
/// header with every call. A key can only touch the accounts it is tied to. Every order goes through the same
/// checks as one from the ticket, and sending the same clientOrderId twice returns the first order.
/// </summary>
[ApiController]
[Route("api/strategy")]
[AllowAnonymous]
[EnableRateLimiting("strategy")]
public sealed class StrategyController(TradingService trading) : ControllerBase
{
    public const string KeyHeader = "X-KFT-Strategy-Key";

    [HttpPost("orders")]
    public async Task<IActionResult> SubmitAsync(SubmitOrderRequest request, CancellationToken cancellationToken)
    {
        if (await KeyAsync(cancellationToken) is not { } key)
        {
            return Unauthorized(new ErrorResponse($"Send a valid strategy key in the {KeyHeader} header."));
        }

        return Result(await trading.SubmitAsync(request, new Actor(key.Name, true, false), key, cancellationToken));
    }

    [HttpPost("orders/{id:long}/cancel")]
    public async Task<IActionResult> CancelAsync(long id, CancellationToken cancellationToken)
    {
        if (await KeyAsync(cancellationToken) is not { } key)
        {
            return Unauthorized(new ErrorResponse($"Send a valid strategy key in the {KeyHeader} header."));
        }

        return Result(await trading.CancelAsync(id, new Actor(key.Name, true, false), key, cancellationToken));
    }

    [HttpGet("orders/{id:long}")]
    public async Task<IActionResult> OrderAsync(long id, CancellationToken cancellationToken)
    {
        if (await KeyAsync(cancellationToken) is not { } key)
        {
            return Unauthorized(new ErrorResponse($"Send a valid strategy key in the {KeyHeader} header."));
        }

        OrderDto? order = await trading.OrderAsync(id, cancellationToken);
        return order is null || !TradingService.AccountIds(key).Contains(order.TradingAccountId) ? NotFound() : Ok(order);
    }

    [HttpGet("book/{type}/{asset}")]
    public async Task<IActionResult> BookAsync(InstrumentType type, string asset, CancellationToken cancellationToken) =>
        await KeyAsync(cancellationToken) is null ? Unauthorized(new ErrorResponse($"Send a valid strategy key in the {KeyHeader} header.")) : Ok(await trading.BookAsync(asset, type, cancellationToken));

    private async Task<StrategyKey?> KeyAsync(CancellationToken cancellationToken) => await trading.FindStrategyKeyAsync(Request.Headers[KeyHeader].FirstOrDefault(), cancellationToken);

    private IActionResult Result(TradingChange<OrderDto> change) => change.Done ? Ok(change.Value) : change.NotFound ? NotFound() : UnprocessableEntity(new ErrorResponse(change.Error!));
}
