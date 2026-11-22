using KatalepsisFT.Application.Holdings;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace KatalepsisFT.Web.Funds;

/// <summary>Holdings of a fund on its latest valued day, from the top down to one asset.</summary>
[ApiController]
[Route("api")]
public sealed class FundsController(ISender sender) : ControllerBase
{
    [HttpGet("funds")]
    public async Task<IReadOnlyList<FundListItemDto>> ListAsync(CancellationToken cancellationToken) => await sender.Send(new GetFundsQuery(), cancellationToken);

    [HttpGet("funds/{fundId:int}/overview")]
    public async Task<ActionResult<FundOverviewDto>> OverviewAsync(int fundId, InstrumentFilter filter = InstrumentFilter.All, CancellationToken cancellationToken = default) =>
        OrNotFound(await sender.Send(new GetFundOverviewQuery(fundId, filter), cancellationToken));

    [HttpGet("funds/{fundId:int}/holdings")]
    public async Task<ActionResult<FundHoldingsDto>> HoldingsAsync(int fundId, InstrumentFilter filter = InstrumentFilter.All, CancellationToken cancellationToken = default) =>
        OrNotFound(await sender.Send(new GetFundHoldingsQuery(fundId, filter), cancellationToken));

    [HttpGet("funds/{fundId:int}/assets/{symbol}")]
    public async Task<ActionResult<AssetDetailDto>> AssetAsync(int fundId, string symbol, CancellationToken cancellationToken) =>
        OrNotFound(await sender.Send(new GetAssetQuery(fundId, symbol), cancellationToken));

    [HttpGet("sleeves/{sleeveId:int}")]
    public async Task<ActionResult<SleeveDetailDto>> SleeveAsync(int sleeveId, InstrumentFilter filter = InstrumentFilter.All, CancellationToken cancellationToken = default) =>
        OrNotFound(await sender.Send(new GetSleeveQuery(sleeveId, filter), cancellationToken));

    [HttpGet("managers/{managerId:int}")]
    public async Task<ActionResult<ManagerDetailDto>> ManagerAsync(int managerId, InstrumentFilter filter = InstrumentFilter.All, CancellationToken cancellationToken = default) =>
        OrNotFound(await sender.Send(new GetManagerQuery(managerId, filter), cancellationToken));

    private ActionResult<T> OrNotFound<T>(T? value)
        where T : class => value is null ? NotFound() : value;
}
