using KatalepsisFT.Application.Performance;
using KatalepsisFT.Application.Risk;
using Microsoft.AspNetCore.Mvc;

namespace KatalepsisFT.Web.Risk;

/// <summary>Risk and liquidity of a fund, a sleeve or a manager on its latest valued day.</summary>
[ApiController]
[Route("api")]
public sealed class RiskController(RiskService risk) : ControllerBase
{
    [HttpGet("risk/{scope}/{id:int}")]
    public async Task<ActionResult<RiskDto>> RiskAsync(PerformanceScope scope, int id, CancellationToken cancellationToken)
    {
        RiskDto? result = await risk.GetAsync(scope, id, cancellationToken);
        return result is null ? NotFound() : result;
    }

    /// <param name="participation">Share of an asset's average daily volume we are willing to trade, between 0.01 and 1.</param>
    [HttpGet("liquidity/{scope}/{id:int}")]
    public async Task<ActionResult<LiquidityDto>> LiquidityAsync(PerformanceScope scope, int id, decimal participation = RiskService.DefaultParticipation, CancellationToken cancellationToken = default)
    {
        if (participation < 0.01m || participation > 1m)
        {
            return BadRequest(new KatalepsisFT.Application.Auth.ErrorResponse("Participation must be between 0.01 and 1."));
        }

        LiquidityDto? result = await risk.GetLiquidityAsync(scope, id, participation, cancellationToken);
        return result is null ? NotFound() : result;
    }
}
