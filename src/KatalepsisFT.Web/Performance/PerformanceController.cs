using KatalepsisFT.Application.Performance;
using Microsoft.AspNetCore.Mvc;

namespace KatalepsisFT.Web.Performance;

/// <summary>Returns against a benchmark for a fund, a sleeve, a manager or one asset.</summary>
[ApiController]
[Route("api/performance")]
public sealed class PerformanceController(PerformanceService performance) : ControllerBase
{
    [HttpGet("{scope}/{id:int}")]
    public async Task<ActionResult<PerformanceDto>> GetAsync(PerformanceScope scope, int id, int? benchmarkId = null, PerformancePeriod period = PerformancePeriod.Ytd, CancellationToken cancellationToken = default)
    {
        PerformanceDto? result = await performance.GetAsync(scope, id, benchmarkId, period, cancellationToken);
        return result is null ? NotFound() : result;
    }

    [HttpGet("asset/{symbol}")]
    public async Task<ActionResult<AssetPerformanceDto>> AssetAsync(string symbol, int? benchmarkId = null, PerformancePeriod period = PerformancePeriod.Ytd, CancellationToken cancellationToken = default)
    {
        AssetPerformanceDto? result = await performance.GetAssetAsync(symbol, benchmarkId, period, cancellationToken);
        return result is null ? NotFound() : result;
    }
}
