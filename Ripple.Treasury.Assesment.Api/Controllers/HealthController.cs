using Microsoft.AspNetCore.Mvc;

namespace Ripple.Treasury.Assesment.Controllers;

[ApiController]
[Route("health")]
public class HealthController : ControllerBase
{
    [HttpGet("live")]
    public IActionResult Alive()
    {
        return Ok();
    }
    
    [HttpGet("ready")]
    public IActionResult Index()
    {
        //  TODO - Check DB and Redis
        return Ok();
    }
}