using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Ripple.Treasury.Assessment.Api.Models;

namespace Ripple.Treasury.Assessment.Api.Controllers;

[ApiController]
[Route("health")]
public class HealthController(HealthCheckService healthChecks) : ControllerBase
{
    // Liveness: is the process up. Deliberately checks nothing external
    [HttpGet("live")]
    public IActionResult CheckLiveness()
    {
        return Ok(new HealthResponse { Status = nameof(HealthStatus.Healthy) });
    }

    // Can this instance serve traffic. Runs the checks tagged "ready".
    [HttpGet("ready")]
    [ProducesResponseType(typeof(HealthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(HealthResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CheckReadinessAsync(CancellationToken cancellationToken)
    {
        // Check dependencies
        HealthReport report = await healthChecks.CheckHealthAsync(
            registration => registration.Tags.Contains("ready"), cancellationToken);

        HealthResponse response = new()
        {
            Status = report.Status.ToString(),
            DurationMs = report.TotalDuration.TotalMilliseconds
        };

        foreach (KeyValuePair<string, HealthReportEntry> entry in report.Entries)
        {
            response.Checks.Add(new HealthCheckEntry
            {
                Name = entry.Key,
                Status = entry.Value.Status.ToString(),
                DurationMs = entry.Value.Duration.TotalMilliseconds,
                Error = entry.Value.Exception?.Message
            });
        }

        // return health status
        if (report.Status == HealthStatus.Healthy)
        {
            return Ok(response);
        }

        return StatusCode(StatusCodes.Status503ServiceUnavailable, response);
    }
}
