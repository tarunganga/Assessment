using Microsoft.AspNetCore.Mvc;

namespace Ripple.Treasury.Assesment.Controllers;

[ApiController]
[Route("reports")]
public class ReportsController : ControllerBase
{
    [HttpGet("")]
    public IActionResult Index()
    {
        return Ok();
    }
}