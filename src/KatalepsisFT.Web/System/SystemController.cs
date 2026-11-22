using KatalepsisFT.Domain.Access;
using KatalepsisFT.Infrastructure.Health;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KatalepsisFT.Web.System;

[ApiController]
[Route("api/system")]
[Authorize(Roles = Roles.Admin)]
public sealed class SystemController(ReadinessProbe probe) : ControllerBase
{
    /// <summary>What this instance can reach, and when it last heard each worker.</summary>
    [HttpGet("status")]
    public async Task<ReadinessReport> StatusAsync(CancellationToken cancellationToken) => await probe.CheckAsync(cancellationToken);
}
